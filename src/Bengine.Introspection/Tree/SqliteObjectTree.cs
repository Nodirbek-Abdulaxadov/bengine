namespace Bengine.Introspection.Tree;

using System.Data.Common;
using Bengine.Enums;

/// <summary>
/// The SQLite object tree: a server (the database file) holds its attached
/// databases; a database holds Tables and Views; a table holds Columns,
/// Constraints, Indexes, and Triggers. SQL comes straight from
/// <c>sqlite_master</c>, which stores each object's original <c>CREATE</c>
/// statement.
/// </summary>
public sealed class SqliteObjectTree : ObjectTreeBase
{
    /// <summary>Creates the tree over an open SQLite connection.</summary>
    /// <param name="connection">An open SQLite connection.</param>
    public SqliteObjectTree(DbConnection connection)
        : base(connection, DatabaseProvider.Sqlite)
    {
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<TreeNode>> GetChildrenAsync(NodeRef node, CancellationToken cancellationToken = default) =>
        node.Kind switch
        {
            NodeKind.Server => ServerChildrenAsync(node, cancellationToken),
            NodeKind.DatabasesFolder => DatabasesAsync(node, cancellationToken),
            NodeKind.Database => DatabaseChildrenAsync(node, cancellationToken),
            NodeKind.TablesFolder => MasterAsync(node, NodeKind.Table, "table", cancellationToken),
            NodeKind.ViewsFolder => MasterAsync(node, NodeKind.View, "view", cancellationToken),
            NodeKind.Table => TableChildrenAsync(node, cancellationToken),
            NodeKind.View => ViewChildrenAsync(node, cancellationToken),
            NodeKind.ColumnsFolder => ColumnsAsync(node, cancellationToken),
            NodeKind.ConstraintsFolder => ConstraintsAsync(node, cancellationToken),
            NodeKind.IndexesFolder => IndexesAsync(node, cancellationToken),
            NodeKind.TriggersFolder => TriggersAsync(node, cancellationToken),
            _ => Task.FromResult<IReadOnlyList<TreeNode>>([]),
        };

    /// <inheritdoc />
    public override Task<ObjectProperties> GetPropertiesAsync(NodeRef node, CancellationToken cancellationToken = default) =>
        node.Kind switch
        {
            NodeKind.Server => ServerPropertiesAsync(cancellationToken),
            NodeKind.Database => DatabasePropertiesAsync(node, cancellationToken),
            NodeKind.Table or NodeKind.View => MasterPropertiesAsync(node, node.Name!, cancellationToken),
            NodeKind.Index or NodeKind.Trigger => MasterPropertiesAsync(node, node.Name!, cancellationToken),
            NodeKind.Column => ColumnPropertiesAsync(node, cancellationToken),
            NodeKind.PrimaryKey or NodeKind.ForeignKey or NodeKind.UniqueConstraint => ConstraintPropertiesAsync(node, cancellationToken),
            _ => Task.FromResult(new ObjectProperties { Title = node.Name ?? node.Kind.ToString() }),
        };

    private static string Db(NodeRef node) => node.Database ?? "main";

    private static string QuoteDb(NodeRef node) => "\"" + Db(node).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private async Task<IReadOnlyList<TreeNode>> ServerChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var count = await TryScalarAsync("SELECT count(*) FROM pragma_database_list", ct).ConfigureAwait(false);
        return [Folder(node, NodeKind.DatabasesFolder, "Databases", count is null ? null : Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture))];
    }

    private async Task<IReadOnlyList<TreeNode>> DatabasesAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            "SELECT name, file FROM pragma_database_list ORDER BY seq",
            reader =>
            {
                var name = Str(reader, "name");
                return new TreeNode
                {
                    // The main database is unqualified; attached ones qualify by their alias.
                    Ref = folder.Item(NodeKind.Database, name) with { Database = name, Schema = name == "main" ? null : name },
                    Label = name,
                    Detail = string.IsNullOrEmpty(StrOrNull(reader, "file")) ? null : Path.GetFileName(Str(reader, "file")),
                    HasChildren = true,
                };
            },
            ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> DatabaseChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var counts = await RowAsync(
            $"""
            SELECT (SELECT count(*) FROM {QuoteDb(node)}.sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\_%' ESCAPE '\') AS tables,
                   (SELECT count(*) FROM {QuoteDb(node)}.sqlite_master WHERE type = 'view') AS views
            """,
            ct).ConfigureAwait(false);

        return
        [
            Folder(node, NodeKind.TablesFolder, "Tables", Long(counts, "tables")),
            Folder(node, NodeKind.ViewsFolder, "Views", Long(counts, "views")),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> MasterAsync(NodeRef folder, NodeKind kind, string type, CancellationToken ct) =>
        await QueryAsync(
            $"SELECT name FROM {QuoteDb(folder)}.sqlite_master WHERE type = @type AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name",
            reader =>
            {
                var name = Str(reader, "name");
                return new TreeNode { Ref = folder.Item(kind, name) with { Relation = name }, Label = name, HasChildren = true };
            },
            ct,
            ("@type", type)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> TableChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var counts = await RowAsync(
            $"""
            SELECT (SELECT count(*) FROM pragma_table_info(@r, @db)) AS columns,
                   (SELECT count(DISTINCT id) FROM pragma_foreign_key_list(@r, @db))
                     + (SELECT CASE WHEN EXISTS (SELECT 1 FROM pragma_table_info(@r, @db) WHERE pk > 0) THEN 1 ELSE 0 END)
                     + (SELECT count(*) FROM pragma_index_list(@r, @db) WHERE origin = 'u') AS constraints,
                   (SELECT count(*) FROM pragma_index_list(@r, @db) WHERE origin = 'c') AS indexes,
                   (SELECT count(*) FROM {QuoteDb(node)}.sqlite_master WHERE type = 'trigger' AND tbl_name = @r) AS triggers
            """,
            ct,
            ("@r", node.Relation),
            ("@db", Db(node))).ConfigureAwait(false);

        return
        [
            Folder(node, NodeKind.ColumnsFolder, "Columns", Long(counts, "columns")),
            Folder(node, NodeKind.ConstraintsFolder, "Constraints", Long(counts, "constraints")),
            Folder(node, NodeKind.IndexesFolder, "Indexes", Long(counts, "indexes")),
            Folder(node, NodeKind.TriggersFolder, "Triggers", Long(counts, "triggers")),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> ViewChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var columns = await TryScalarAsync("SELECT count(*) FROM pragma_table_info(@r, @db)", ct, ("@r", node.Relation), ("@db", Db(node))).ConfigureAwait(false);
        return [Folder(node, NodeKind.ColumnsFolder, "Columns", columns is null ? null : Convert.ToInt64(columns, System.Globalization.CultureInfo.InvariantCulture))];
    }

    private async Task<IReadOnlyList<TreeNode>> ColumnsAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            """
            SELECT c.name, c.type, c.pk,
                   EXISTS (SELECT 1 FROM pragma_foreign_key_list(@r, @db) f WHERE f."from" = c.name) AS fk
            FROM pragma_table_info(@r, @db) c ORDER BY c.cid
            """,
            reader => Item(
                folder,
                NodeKind.Column,
                Str(reader, "name"),
                detail: Str(reader, "type").ToLowerInvariant(),
                badge: (LongOrNull(reader, "pk") ?? 0) > 0 ? "PK" : Bool(reader, "fk") ? "FK" : null),
            ct,
            ("@r", folder.Relation),
            ("@db", Db(folder))).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> ConstraintsAsync(NodeRef folder, CancellationToken ct)
    {
        var nodes = new List<TreeNode>();
        var hasPk = await TryScalarAsync("SELECT count(*) FROM pragma_table_info(@r, @db) WHERE pk > 0", ct, ("@r", folder.Relation), ("@db", Db(folder))).ConfigureAwait(false);
        if (hasPk is not null && Convert.ToInt64(hasPk, System.Globalization.CultureInfo.InvariantCulture) > 0)
        {
            nodes.Add(Item(folder, NodeKind.PrimaryKey, $"{folder.Relation}_pkey"));
        }

        nodes.AddRange(await QueryAsync(
            "SELECT id, \"table\" AS target, group_concat(\"from\", ', ') AS cols FROM pragma_foreign_key_list(@r, @db) GROUP BY id ORDER BY id",
            reader => Item(
                folder,
                NodeKind.ForeignKey,
                $"{folder.Relation}_{Str(reader, "cols").Replace(", ", "_", StringComparison.Ordinal)}_fkey",
                detail: $"→ {Str(reader, "target")}",
                signature: Str(reader, "id")),
            ct,
            ("@r", folder.Relation),
            ("@db", Db(folder))).ConfigureAwait(false));

        nodes.AddRange(await QueryAsync(
            "SELECT name FROM pragma_index_list(@r, @db) WHERE origin = 'u' ORDER BY name",
            reader => Item(folder, NodeKind.UniqueConstraint, Str(reader, "name")),
            ct,
            ("@r", folder.Relation),
            ("@db", Db(folder))).ConfigureAwait(false));

        return nodes;
    }

    private async Task<IReadOnlyList<TreeNode>> IndexesAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            "SELECT name, \"unique\" FROM pragma_index_list(@r, @db) WHERE origin = 'c' ORDER BY name",
            reader => Item(folder, NodeKind.Index, Str(reader, "name"), detail: Bool(reader, "unique") ? "unique" : null),
            ct,
            ("@r", folder.Relation),
            ("@db", Db(folder))).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> TriggersAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            $"SELECT name FROM {QuoteDb(folder)}.sqlite_master WHERE type = 'trigger' AND tbl_name = @r ORDER BY name",
            reader => Item(folder, NodeKind.Trigger, Str(reader, "name")),
            ct,
            ("@r", folder.Relation)).ConfigureAwait(false);

    private async Task<ObjectProperties> ServerPropertiesAsync(CancellationToken ct)
    {
        var row = await RowAsync(
            "SELECT sqlite_version() AS version, (SELECT file FROM pragma_database_list WHERE name = 'main') AS file",
            ct).ConfigureAwait(false);
        return new ObjectProperties
        {
            Title = "SQLite",
            Properties = Props(("Version", Get(row, "version")), ("File", Get(row, "file"))),
        };
    }

    private async Task<ObjectProperties> DatabasePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var db = QuoteDb(node);
        var row = await RowAsync(
            $"""
            SELECT (SELECT file FROM pragma_database_list WHERE name = @db) AS file,
                   (SELECT page_size FROM pragma_page_size) AS page_size,
                   (SELECT page_count FROM pragma_page_count) AS page_count,
                   (SELECT encoding FROM pragma_encoding) AS encoding,
                   (SELECT journal_mode FROM pragma_journal_mode) AS journal_mode,
                   (SELECT user_version FROM pragma_user_version) AS user_version,
                   (SELECT count(*) FROM {db}.sqlite_master WHERE type = 'table') AS tables
            """,
            ct,
            ("@db", Db(node))).ConfigureAwait(false);

        var size = Long(row, "page_size") * Long(row, "page_count");
        return new ObjectProperties
        {
            Title = Db(node),
            Properties = Props(
                ("Name", Db(node)),
                ("File", Get(row, "file")),
                ("Size", size is { } bytes ? FormatBytes(bytes) : null),
                ("Encoding", Get(row, "encoding")),
                ("Journal mode", Get(row, "journal_mode")),
                ("Page size", Get(row, "page_size")),
                ("User version", Get(row, "user_version")),
                ("Tables", Get(row, "tables"))),
        };
    }

    private async Task<ObjectProperties> MasterPropertiesAsync(NodeRef node, string name, CancellationToken ct)
    {
        var row = await RowAsync(
            $"SELECT type, tbl_name, sql FROM {QuoteDb(node)}.sqlite_master WHERE name = @n",
            ct,
            ("@n", name)).ConfigureAwait(false);

        long? rows = null;
        if (node.Kind == NodeKind.Table)
        {
            var count = await TryScalarAsync($"SELECT count(*) FROM {QuoteDb(node)}.\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"", ct).ConfigureAwait(false);
            rows = count is null ? null : Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture);
        }

        var sql = Get(row, "sql");
        return new ObjectProperties
        {
            Title = name,
            Properties = Props(("Name", name), ("Type", Get(row, "type")), ("Table", node.Kind is NodeKind.Index or NodeKind.Trigger ? Get(row, "tbl_name") : null), ("Rows", rows?.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            Sql = sql is null ? $"-- {name} has no stored definition (it was created implicitly)." : $"-- {Get(row, "type")}: {name}\n\n{sql};",
        };
    }

    private async Task<ObjectProperties> ColumnPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            "SELECT cid, type, \"notnull\", dflt_value, pk FROM pragma_table_info(@r, @db) WHERE name = @n",
            ct,
            ("@r", node.Relation),
            ("@db", Db(node)),
            ("@n", node.Name)).ConfigureAwait(false);

        return new ObjectProperties
        {
            Title = $"{node.Relation}.{node.Name}",
            Properties = Props(
                ("Name", node.Name),
                ("Position", (Long(row, "cid") + 1)?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("Data type", Get(row, "type")),
                ("Not NULL", Long(row, "notnull") == 1 ? "Yes" : "No"),
                ("Default", Get(row, "dflt_value")),
                ("Primary key", Long(row, "pk") > 0 ? "Yes" : "No")),
            Sql = $"-- SQLite cannot add constraints to an existing column; this is the column's declaration.\n\n\"{node.Name}\" {Get(row, "type")}" +
                  (Long(row, "notnull") == 1 ? " NOT NULL" : string.Empty) +
                  (Get(row, "dflt_value") is { } dflt ? $" DEFAULT {dflt}" : string.Empty),
        };
    }

    private async Task<ObjectProperties> ConstraintPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        switch (node.Kind)
        {
            case NodeKind.PrimaryKey:
            {
                var cols = await QueryAsync(
                    "SELECT name FROM pragma_table_info(@r, @db) WHERE pk > 0 ORDER BY pk",
                    reader => Str(reader, "name"),
                    ct,
                    ("@r", node.Relation),
                    ("@db", Db(node))).ConfigureAwait(false);
                return new ObjectProperties
                {
                    Title = node.Name!,
                    Properties = Props(("Type", "Primary key"), ("Columns", string.Join(", ", cols))),
                    Sql = $"PRIMARY KEY ({string.Join(", ", cols.Select(c => $"\"{c}\""))})",
                };
            }

            case NodeKind.ForeignKey:
            {
                var rows = await QueryAsync(
                    "SELECT \"table\" AS target, \"from\" AS src, \"to\" AS dst, on_update, on_delete FROM pragma_foreign_key_list(@r, @db) WHERE id = @id ORDER BY seq",
                    reader => (Target: Str(reader, "target"), From: Str(reader, "src"), To: Str(reader, "dst"), OnUpdate: Str(reader, "on_update"), OnDelete: Str(reader, "on_delete")),
                    ct,
                    ("@r", node.Relation),
                    ("@db", Db(node)),
                    ("@id", long.TryParse(node.Signature, out var id) ? id : -1)).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    return new ObjectProperties { Title = node.Name! };
                }

                var first = rows[0];
                var from = string.Join(", ", rows.Select(r => $"\"{r.From}\""));
                var to = string.Join(", ", rows.Select(r => $"\"{r.To}\""));
                return new ObjectProperties
                {
                    Title = node.Name!,
                    Properties = Props(("Type", "Foreign key"), ("Columns", string.Join(", ", rows.Select(r => r.From))), ("References", $"{first.Target} ({string.Join(", ", rows.Select(r => r.To))})"), ("On update", first.OnUpdate), ("On delete", first.OnDelete)),
                    Sql = $"FOREIGN KEY ({from}) REFERENCES \"{first.Target}\" ({to}) ON UPDATE {first.OnUpdate} ON DELETE {first.OnDelete}",
                };
            }

            default:
            {
                var cols = await QueryAsync(
                    "SELECT name FROM pragma_index_info(@n, @db) ORDER BY seqno",
                    reader => Str(reader, "name"),
                    ct,
                    ("@n", node.Name),
                    ("@db", Db(node))).ConfigureAwait(false);
                return new ObjectProperties
                {
                    Title = node.Name!,
                    Properties = Props(("Type", "Unique"), ("Columns", string.Join(", ", cols))),
                    Sql = $"UNIQUE ({string.Join(", ", cols.Select(c => $"\"{c}\""))})",
                };
            }
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} kB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };
}
