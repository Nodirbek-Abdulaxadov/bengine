namespace Bengine.Studio.ViewModels;

using Bengine.Introspection;
using Bengine.Introspection.Tree;

/// <summary>
/// Lays the engine's pgAdmin-shaped tree out the way DataGrip's Database
/// Explorer shows it: databases straight under the data source, schemas
/// straight under the database, columns straight under the table; object
/// kinds as lower-case folders with counts, empty ones hidden; functions,
/// procedures, and trigger functions together as <c>routines</c>;
/// constraints split into <c>keys</c>, <c>foreign keys</c>, and
/// <c>checks</c>; and the server- and database-wide objects (roles,
/// tablespaces, extensions, casts, system schemas, …) tucked into
/// <c>server objects</c> and <c>database objects</c>.
/// </summary>
public sealed partial class TreeItemViewModel
{
    // Folders whose children take their place.
    private static readonly HashSet<NodeKind> Inlined = [NodeKind.DatabasesFolder, NodeKind.SchemasFolder, NodeKind.ColumnsFolder];

    private static readonly HashSet<NodeKind> Routines = [NodeKind.FunctionsFolder, NodeKind.ProceduresFolder, NodeKind.TriggerFunctionsFolder];

    // A PostgreSQL database's own objects, as opposed to its schemas.
    private static readonly HashSet<NodeKind> DatabaseWide =
    [
        NodeKind.CastsFolder, NodeKind.CatalogsFolder, NodeKind.EventTriggersFolder, NodeKind.ExtensionsFolder,
        NodeKind.ForeignDataWrappersFolder, NodeKind.ForeignServersFolder, NodeKind.LanguagesFolder,
        NodeKind.PublicationsFolder, NodeKind.SubscriptionsFolder,
    ];

    // DataGrip's order of object kinds inside a schema (and a table).
    private static readonly NodeKind[] Order =
    [
        NodeKind.TablesFolder, NodeKind.ViewsFolder, NodeKind.MaterializedViewsFolder, NodeKind.ForeignTablesFolder,
        NodeKind.FunctionsFolder, NodeKind.AggregatesFolder, NodeKind.SequencesFolder, NodeKind.TypesFolder,
        NodeKind.DomainsFolder, NodeKind.CollationsFolder, NodeKind.EventsFolder, NodeKind.IndexesFolder,
        NodeKind.TriggersFolder,
    ];

    /// <summary>Gets the DataGrip label of an engine folder (<c>Tables</c> → <c>tables</c>).</summary>
    /// <param name="node">The folder node.</param>
    /// <returns>The label.</returns>
    public static string FolderLabel(TreeNode node) => node.Ref.Kind switch
    {
        NodeKind.RolesFolder => "roles",
        NodeKind.CatalogsFolder => "system schemas",
        NodeKind.TriggerFunctionsFolder => "trigger functions",
        _ => node.Label.ToLowerInvariant(),
    };

    // A folder of the layout that is not an engine node: its children come from a loader.
    private TreeItemViewModel Virtual(string label, long? count, Func<Task<IReadOnlyList<TreeNode>?>> loader)
    {
        var item = new TreeItemViewModel(_owner, this, label)
        {
            Server = Server,
            CountText = count?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        item._virtualKind = "Folder";
        item._loader = loader;
        item.Children.Add(Placeholder(_owner, item, "Loading…"));
        return item;
    }

    // Turns fetched engine nodes into this row's children, fetching inlined folders on the way.
    private async Task<List<TreeItemViewModel>?> ArrangeAsync(IReadOnlyList<TreeNode> nodes)
    {
        var server = Server!;
        var items = new List<(double Rank, TreeItemViewModel Item)>();
        var serverObjects = new List<TreeNode>();
        var databaseObjects = new List<TreeNode>();
        var routines = new List<TreeNode>();
        var grouping = _virtualKind is null;
        var parentKind = Node?.Ref.Kind;
        var indexes = Array.IndexOf(Order, NodeKind.IndexesFolder);

        double RankOf(NodeKind kind) => Array.IndexOf(Order, kind) is var i and >= 0 ? i : Order.Length;
        Task<IReadOnlyList<TreeNode>?> Fixed(IReadOnlyList<TreeNode> list) => Task.FromResult<IReadOnlyList<TreeNode>?>(list);

        foreach (var node in nodes)
        {
            var kind = node.Ref.Kind;
            if (node.IsFolder && node.Count == 0)
            {
                continue;
            }

            if (Inlined.Contains(kind))
            {
                // Databases, schemas, and columns come first.
                var inner = await _owner.FetchChildrenAsync(server, node.Ref);
                if (inner is null)
                {
                    return null;
                }

                items.AddRange(inner.Select(child => (-1d, ForNode(_owner, server, child, this))));
            }
            else if (grouping && parentKind == NodeKind.Server && node.IsFolder)
            {
                serverObjects.Add(node);
            }
            else if (grouping && DatabaseWide.Contains(kind))
            {
                databaseObjects.Add(node);
            }
            else if (grouping && Routines.Contains(kind))
            {
                routines.Add(node);
            }
            else if (kind == NodeKind.ConstraintsFolder)
            {
                var constraints = await _owner.FetchChildrenAsync(server, node.Ref);
                if (constraints is null)
                {
                    return null;
                }

                // DataGrip's table order: columns, keys, foreign keys, indexes, checks, triggers.
                foreach (var (label, rank, kinds) in new (string, double, NodeKind[])[]
                {
                    ("keys", indexes - 0.6, [NodeKind.PrimaryKey, NodeKind.UniqueConstraint]),
                    ("foreign keys", indexes - 0.3, [NodeKind.ForeignKey]),
                    ("checks", indexes + 0.3, [NodeKind.CheckConstraint]),
                    ("exclusion constraints", indexes + 0.6, [NodeKind.ExclusionConstraint]),
                })
                {
                    var group = constraints.Where(c => kinds.Contains(c.Ref.Kind)).ToList();
                    if (group.Count > 0)
                    {
                        items.Add((rank, Virtual(label, group.Count, () => Fixed(group))));
                    }
                }
            }
            else
            {
                items.Add((node.IsFolder ? RankOf(kind) : -1, ForNode(_owner, server, node, this)));
            }
        }

        if (routines.Count > 0)
        {
            items.Add((RankOf(NodeKind.FunctionsFolder), Virtual("routines", routines.Sum(r => r.Count ?? 0), () => UnionAsync(server, routines))));
        }

        if (databaseObjects.Count > 0)
        {
            items.Add((Order.Length + 1, Virtual("database objects", null, () => Fixed(databaseObjects))));
        }

        if (serverObjects.Count > 0)
        {
            items.Add((Order.Length + 1, Virtual("server objects", null, () => Fixed(serverObjects))));
        }

        // OrderBy is stable, so each group keeps the server's own order.
        return items.OrderBy(i => i.Rank).Select(i => i.Item).ToList();
    }

    // The children of several folders as one list (routines).
    private async Task<IReadOnlyList<TreeNode>?> UnionAsync(Bengine.Introspection.Servers.ServerProfile server, IReadOnlyList<TreeNode> folders)
    {
        var all = new List<TreeNode>();
        foreach (var folder in folders)
        {
            if (await _owner.FetchChildrenAsync(server, folder.Ref) is not { } children)
            {
                return null;
            }

            all.AddRange(children);
        }

        return all.OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
