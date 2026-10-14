namespace Bengine.Introspection.Mongo;

using System.Globalization;
using System.Text;
using Bengine.Introspection.Tree;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// The object explorer for MongoDB: databases with their collections and
/// views; a collection's fields (inferred from sampled documents, embedded
/// documents expandable) and indexes; and the server's users. A collection is
/// a <see cref="NodeKind.Table"/> and a field a <see cref="NodeKind.Column"/>,
/// so the tree's open, data, and console actions apply. An object's "DDL" is
/// the shell script that recreates it (<c>db.createCollection</c>,
/// <c>createIndex</c>, <c>db.createView</c>).
/// </summary>
public sealed class MongoObjectTree : IObjectTree
{
    private readonly MongoConnection _connection;

    /// <summary>Creates the tree over a connection to the database the nodes belong to.</summary>
    /// <param name="connection">The connection.</param>
    public MongoObjectTree(MongoConnection connection) => _connection = connection;

    /// <inheritdoc />
    public Task<IReadOnlyList<TreeNode>> GetChildrenAsync(NodeRef node, CancellationToken cancellationToken = default) => GuardAsync(() => node.Kind switch
    {
        NodeKind.Server => ServerChildrenAsync(node, cancellationToken),
        NodeKind.DatabasesFolder => DatabasesAsync(node, cancellationToken),
        NodeKind.Database => DatabaseChildrenAsync(node, cancellationToken),
        NodeKind.TablesFolder => CollectionsAsync(node, NodeKind.Table, cancellationToken),
        NodeKind.ViewsFolder => CollectionsAsync(node, NodeKind.View, cancellationToken),
        NodeKind.Table or NodeKind.View => CollectionChildrenAsync(node, cancellationToken),
        NodeKind.ColumnsFolder => FieldsAsync(node, node.Relation!, null, cancellationToken),
        NodeKind.Column => FieldsAsync(node.Folder(NodeKind.ColumnsFolder), node.Relation!, node.Name, cancellationToken),
        NodeKind.IndexesFolder => IndexesAsync(node, cancellationToken),
        NodeKind.RolesFolder => UsersAsync(node, cancellationToken),
        _ => Task.FromResult<IReadOnlyList<TreeNode>>([]),
    });

    /// <inheritdoc />
    public Task<ObjectProperties> GetPropertiesAsync(NodeRef node, CancellationToken cancellationToken = default) => GuardAsync(() => node.Kind switch
    {
        NodeKind.Server => ServerPropertiesAsync(cancellationToken),
        NodeKind.Database => DatabasePropertiesAsync(node, cancellationToken),
        NodeKind.Table or NodeKind.View => CollectionPropertiesAsync(node, cancellationToken),
        NodeKind.Column => FieldPropertiesAsync(node, cancellationToken),
        NodeKind.Index => IndexPropertiesAsync(node, cancellationToken),
        NodeKind.LoginRole => UserPropertiesAsync(node, cancellationToken),
        _ => Task.FromResult(new ObjectProperties { Title = node.Name ?? node.Kind.ToString() }),
    });

    /// <summary>Renders the shell script that recreates a collection: <c>db.createCollection</c> with its options, then its indexes.</summary>
    /// <param name="info">The collection's <c>listCollections</c> entry.</param>
    /// <param name="indexes">Its <c>listIndexes</c> entries.</param>
    /// <returns>The script.</returns>
    public static string CollectionScript(BsonDocument info, IReadOnlyList<BsonDocument> indexes)
    {
        var name = info["name"].AsString;
        var options = info.GetValue("options", new BsonDocument()).AsBsonDocument;
        var script = new StringBuilder();
        if (info.GetValue("type", "collection").AsString == "view")
        {
            var view = options.DeepClone().AsBsonDocument;
            var source = view.GetValue("viewOn", string.Empty).AsString;
            var pipeline = view.GetValue("pipeline", new BsonArray());
            view.Remove("viewOn");
            view.Remove("pipeline");
            script.Append($"db.createView({MongoValues.Quote(name)}, {MongoValues.Quote(source)}, {MongoValues.Shell(pipeline)}");
            script.Append(view.ElementCount > 0 ? $", {MongoValues.Shell(view)});" : ");").AppendLine();
            return script.ToString();
        }

        script.Append($"db.createCollection({MongoValues.Quote(name)}");
        script.Append(options.ElementCount > 0 ? $", {MongoValues.Shell(options)});" : ");").AppendLine();
        foreach (var index in indexes.Where(i => i.GetValue("name", "_id_").AsString != "_id_"))
        {
            script.AppendLine(IndexScript(name, index));
        }

        return script.ToString();
    }

    /// <summary>Renders the <c>createIndex</c> call that recreates an index.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="index">The index's <c>listIndexes</c> entry.</param>
    /// <returns>The command.</returns>
    public static string IndexScript(string collection, BsonDocument index)
    {
        var options = index.DeepClone().AsBsonDocument;
        var key = options["key"];
        options.Remove("key");
        options.Remove("v");
        return $"{MongoShell.Collection(collection)}.createIndex({MongoValues.Shell(key)}, {MongoValues.Shell(options)});";
    }

    private static string Size(BsonValue? bytes)
    {
        if (bytes is null || !bytes.IsNumeric)
        {
            return string.Empty;
        }

        var value = bytes.ToDouble();
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string? Text(BsonValue? value) => value switch
    {
        null or BsonNull => null,
        BsonString text => text.Value,
        BsonDocument or BsonArray => MongoValues.Shell(value),
        _ => MongoValues.ToCell(value) is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : MongoValues.ToCell(value)?.ToString(),
    };

    private static TreeNode Folder(NodeRef parent, NodeKind kind, string label, long? count) => new()
    {
        Ref = parent.Folder(kind),
        Label = label,
        Count = count,
        HasChildren = count is null or > 0,
    };

    private static async Task<T> GuardAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            throw new IntrospectionException(MongoConnection.Message(ex), ex);
        }
    }

    private async Task<BsonDocument> CommandAsync(IMongoDatabase database, BsonDocument command, CancellationToken ct) =>
        await database.RunCommandAsync<BsonDocument>(command, cancellationToken: ct).ConfigureAwait(false);

    private async Task<List<BsonDocument>> DatabaseInfosAsync(CancellationToken ct)
    {
        var reply = await CommandAsync(_connection.Admin, new BsonDocument { { "listDatabases", 1 }, { "authorizedDatabases", true } }, ct).ConfigureAwait(false);
        return reply["databases"].AsBsonArray.OfType<BsonDocument>().OrderBy(d => d["name"].AsString, StringComparer.Ordinal).ToList();
    }

    private async Task<List<BsonDocument>?> UserInfosAsync(CancellationToken ct)
    {
        try
        {
            var reply = await CommandAsync(_connection.Admin, new BsonDocument("usersInfo", new BsonDocument("forAllDBs", true)), ct).ConfigureAwait(false);
            return reply["users"].AsBsonArray.OfType<BsonDocument>().ToList();
        }
        catch (MongoCommandException)
        {
            // Listing users needs the viewUser privilege; without it the folder is left out.
            return null;
        }
    }

    private async Task<IReadOnlyList<TreeNode>> ServerChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var databases = await DatabaseInfosAsync(ct).ConfigureAwait(false);
        var children = new List<TreeNode> { Folder(node, NodeKind.DatabasesFolder, "Databases", databases.Count) };
        if (await UserInfosAsync(ct).ConfigureAwait(false) is { } users)
        {
            children.Add(Folder(node, NodeKind.RolesFolder, "Users", users.Count));
        }

        return children;
    }

    private async Task<IReadOnlyList<TreeNode>> DatabasesAsync(NodeRef folder, CancellationToken ct) =>
        (await DatabaseInfosAsync(ct).ConfigureAwait(false))
            .Select(d => new TreeNode
            {
                Ref = folder.Item(NodeKind.Database, d["name"].AsString) with { Database = d["name"].AsString },
                Label = d["name"].AsString,
                Detail = Size(d.GetValue("sizeOnDisk", BsonNull.Value)),
                HasChildren = true,
            })
            .ToList();

    private async Task<IReadOnlyList<TreeNode>> DatabaseChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var collections = await MongoSchemaProvider.ListAsync(_connection.Database, ct).ConfigureAwait(false);
        return
        [
            Folder(node, NodeKind.TablesFolder, "Collections", collections.Count(c => !c.IsView)),
            Folder(node, NodeKind.ViewsFolder, "Views", collections.Count(c => c.IsView)),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> CollectionsAsync(NodeRef folder, NodeKind kind, CancellationToken ct)
    {
        var collections = await MongoSchemaProvider.ListAsync(_connection.Database, ct).ConfigureAwait(false);
        return collections
            .Where(c => c.IsView == (kind == NodeKind.View))
            .Select(c => new TreeNode
            {
                Ref = folder.Item(kind, c.Name) with { Relation = c.Name },
                Label = c.Name,
                Detail = c.Info.GetValue("type", "collection").AsString is "timeseries" ? "time series"
                    : c.Info.GetValue("options", new BsonDocument()).AsBsonDocument.GetValue("capped", false).ToBoolean() ? "capped" : null,
                HasChildren = true,
            })
            .ToList();
    }

    private async Task<IReadOnlyList<TreeNode>> CollectionChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var isView = node.Kind == NodeKind.View;
        var sample = await MongoFields.SampleAsync(_connection.Database, node.Name!, isView, MongoFields.SampleSize, ct).ConfigureAwait(false);
        var children = new List<TreeNode> { Folder(node, NodeKind.ColumnsFolder, "Fields", MongoFields.Infer(sample).Count) };
        if (!isView)
        {
            var indexes = await (await _connection.Collection(node.Name!).Indexes.ListAsync(ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false);
            children.Add(Folder(node, NodeKind.IndexesFolder, "Indexes", indexes.Count));
        }

        return children;
    }

    private async Task<IReadOnlyList<TreeNode>> FieldsAsync(NodeRef folder, string collection, string? path, CancellationToken ct)
    {
        var isView = await IsViewAsync(collection, ct).ConfigureAwait(false);
        var sample = await MongoFields.SampleAsync(_connection.Database, collection, isView, MongoFields.SampleSize, ct).ConfigureAwait(false);
        return MongoFields.Infer(sample, path)
            .Select(f => new TreeNode
            {
                Ref = folder.Item(NodeKind.Column, f.Path),
                Label = f.Name,
                Detail = f.TypeLabel + (f.IsOptional && f.Path != "_id" ? "?" : string.Empty),
                Badge = f.Path == "_id" ? "PK" : null,
                HasChildren = f.HasChildren,
            })
            .ToList();
    }

    private async Task<bool> IsViewAsync(string collection, CancellationToken ct)
    {
        var options = new ListCollectionsOptions { Filter = new BsonDocument("name", collection) };
        var infos = await (await _connection.Database.ListCollectionsAsync(options, ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false);
        return infos.FirstOrDefault()?.GetValue("type", "collection").AsString == "view";
    }

    private async Task<IReadOnlyList<TreeNode>> IndexesAsync(NodeRef folder, CancellationToken ct)
    {
        var indexes = await (await _connection.Collection(folder.Relation!).Indexes.ListAsync(ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false);
        return indexes
            .Select(i => new TreeNode
            {
                Ref = folder.Item(NodeKind.Index, i["name"].AsString),
                Label = i["name"].AsString,
                Detail = MongoValues.Shell(i["key"]),
                Badge = i.GetValue("unique", false).ToBoolean() ? "UQ" : null,
            })
            .ToList();
    }

    private async Task<IReadOnlyList<TreeNode>> UsersAsync(NodeRef folder, CancellationToken ct) =>
        (await UserInfosAsync(ct).ConfigureAwait(false) ?? [])
            .Select(u => new TreeNode
            {
                Ref = folder.Item(NodeKind.LoginRole, $"{u["user"].AsString}@{u["db"].AsString}"),
                Label = u["user"].AsString,
                Detail = u["db"].AsString,
            })
            .OrderBy(n => n.Label, StringComparer.Ordinal)
            .ToList();

    private async Task<ObjectProperties> ServerPropertiesAsync(CancellationToken ct)
    {
        var build = await CommandAsync(_connection.Admin, new BsonDocument("buildInfo", 1), ct).ConfigureAwait(false);
        var hello = await CommandAsync(_connection.Admin, new BsonDocument("hello", 1), ct).ConfigureAwait(false);
        var properties = new List<PropertyItem>
        {
            new("Version", Text(build.GetValue("version", BsonNull.Value))),
            new("Topology", hello.Contains("setName") ? $"replica set {hello["setName"]}" : hello.GetValue("msg", BsonNull.Value) is BsonString { Value: "isdbgrid" } ? "sharded cluster (mongos)" : "standalone"),
            new("Primary", hello.GetValue("isWritablePrimary", false).ToBoolean() ? "yes" : "no"),
            new("Max BSON size", Size(hello.GetValue("maxBsonObjectSize", BsonNull.Value))),
        };
        return new ObjectProperties { Title = "MongoDB", Properties = properties };
    }

    private async Task<ObjectProperties> DatabasePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var stats = await CommandAsync(_connection.Database, new BsonDocument("dbStats", 1), ct).ConfigureAwait(false);
        return new ObjectProperties
        {
            Title = node.Name!,
            Properties =
            [
                new("Name", node.Name),
                new("Collections", Text(stats.GetValue("collections", BsonNull.Value))),
                new("Views", Text(stats.GetValue("views", BsonNull.Value))),
                new("Documents", Text(stats.GetValue("objects", BsonNull.Value))),
                new("Data size", Size(stats.GetValue("dataSize", BsonNull.Value))),
                new("Storage size", Size(stats.GetValue("storageSize", BsonNull.Value))),
                new("Indexes", Text(stats.GetValue("indexes", BsonNull.Value))),
                new("Index size", Size(stats.GetValue("indexSize", BsonNull.Value))),
            ],
        };
    }

    private async Task<ObjectProperties> CollectionPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var name = node.Name!;
        var options = new ListCollectionsOptions { Filter = new BsonDocument("name", name) };
        var info = (await (await _connection.Database.ListCollectionsAsync(options, ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false)).FirstOrDefault()
            ?? throw new IntrospectionException($"The collection '{name}' no longer exists.");
        var isView = info.GetValue("type", "collection").AsString == "view";
        var indexes = isView ? [] : await (await _connection.Collection(name).Indexes.ListAsync(ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false);
        var properties = new List<PropertyItem> { new("Name", name), new("Type", info.GetValue("type", "collection").AsString) };
        if (isView)
        {
            var viewOptions = info["options"].AsBsonDocument;
            properties.Add(new("View on", Text(viewOptions.GetValue("viewOn", BsonNull.Value))));
            properties.Add(new("Pipeline", Text(viewOptions.GetValue("pipeline", BsonNull.Value))));
        }
        else
        {
            var stats = await MongoFieldsStatsAsync(name, ct).ConfigureAwait(false);
            properties.Add(new("Documents", Text(stats.GetValue("count", BsonNull.Value))));
            properties.Add(new("Data size", Size(stats.GetValue("size", BsonNull.Value))));
            properties.Add(new("Average document", Size(stats.GetValue("avgObjSize", BsonNull.Value))));
            properties.Add(new("Storage size", Size(stats.GetValue("storageSize", BsonNull.Value))));
            properties.Add(new("Indexes", indexes.Count.ToString(CultureInfo.InvariantCulture)));
            properties.Add(new("Index size", Size(stats.GetValue("totalIndexSize", BsonNull.Value))));
            var collectionOptions = info.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (collectionOptions.GetValue("capped", false).ToBoolean())
            {
                properties.Add(new("Capped", $"{Size(collectionOptions.GetValue("size", BsonNull.Value))}, max {Text(collectionOptions.GetValue("max", BsonNull.Value)) ?? "∞"} documents"));
            }

            if (collectionOptions.TryGetValue("validator", out var validator))
            {
                properties.Add(new("Validator", Text(validator)));
            }
        }

        return new ObjectProperties { Title = name, Properties = properties, Sql = CollectionScript(info, indexes) };
    }

    private async Task<BsonDocument> MongoFieldsStatsAsync(string collection, CancellationToken ct)
    {
        try
        {
            var cursor = await _connection.Collection(collection).AggregateAsync(
                PipelineDefinition<BsonDocument, BsonDocument>.Create([new BsonDocument("$collStats", new BsonDocument("storageStats", new BsonDocument()))]),
                cancellationToken: ct).ConfigureAwait(false);
            var stats = await cursor.FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return stats?.GetValue("storageStats", new BsonDocument()).AsBsonDocument ?? [];
        }
        catch (MongoCommandException)
        {
            // $collStats needs the collStats privilege.
            return [];
        }
    }

    private async Task<ObjectProperties> FieldPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var isView = await IsViewAsync(node.Relation!, ct).ConfigureAwait(false);
        var sample = await MongoFields.SampleAsync(_connection.Database, node.Relation!, isView, MongoFields.SampleSize, ct).ConfigureAwait(false);
        var path = node.Name!;
        var parent = path.LastIndexOf('.') is var dot and > 0 ? path[..dot] : null;
        var field = MongoFields.Infer(sample, parent).FirstOrDefault(f => f.Path == path);
        return new ObjectProperties
        {
            Title = path,
            Properties = field is null
                ? [new("Path", path), new("Occurrence", "not in the sampled documents")]
                :
                [
                    new("Path", path),
                    new("Types", field.TypeLabel),
                    new("Occurrence", $"{field.Count} of {field.Sampled} sampled"),
                    new("Collection", node.Relation),
                ],
        };
    }

    private async Task<ObjectProperties> IndexPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var indexes = await (await _connection.Collection(node.Relation!).Indexes.ListAsync(ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false);
        var index = indexes.FirstOrDefault(i => i["name"].AsString == node.Name)
            ?? throw new IntrospectionException($"The index '{node.Name}' no longer exists.");
        var properties = index.Elements.Where(e => e.Name != "v").Select(e => new PropertyItem(e.Name, Text(e.Value))).ToList();
        return new ObjectProperties { Title = node.Name!, Properties = properties, Sql = IndexScript(node.Relation!, index) };
    }

    private async Task<ObjectProperties> UserPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var at = node.Name!.LastIndexOf('@');
        var (user, db) = at > 0 ? (node.Name[..at], node.Name[(at + 1)..]) : (node.Name, "admin");
        var reply = await CommandAsync(_connection.Client.GetDatabase(db), new BsonDocument("usersInfo", user), ct).ConfigureAwait(false);
        var info = reply["users"].AsBsonArray.OfType<BsonDocument>().FirstOrDefault() ?? [];
        return new ObjectProperties
        {
            Title = user,
            Properties =
            [
                new("User", user),
                new("Database", db),
                new("Roles", string.Join(", ", info.GetValue("roles", new BsonArray()).AsBsonArray.OfType<BsonDocument>().Select(r => $"{r["role"]}@{r["db"]}"))),
                new("Mechanisms", Text(info.GetValue("mechanisms", BsonNull.Value))),
            ],
        };
    }
}
