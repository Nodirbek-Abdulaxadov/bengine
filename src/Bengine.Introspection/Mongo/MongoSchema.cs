namespace Bengine.Introspection.Mongo;

using Bengine.Enums;
using Bengine.Introspection.Abstractions;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Model;
using Bengine.Models;
using Bengine.Models.Options;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>A field found in sampled documents.</summary>
/// <param name="Path">The dotted path from the document root.</param>
/// <param name="Name">The field's own name.</param>
/// <param name="Types">The value types seen, most frequent first (<c>null</c> included).</param>
/// <param name="Count">How many sampled documents (or array elements) hold the field.</param>
/// <param name="Sampled">How many documents (or array elements) were looked at.</param>
/// <param name="HasChildren">Whether the field holds documents (directly or in an array).</param>
public sealed record MongoField(string Path, string Name, IReadOnlyList<string> Types, int Count, int Sampled, bool HasChildren)
{
    /// <summary>Gets the type label: one type, or the types joined with <c>|</c>.</summary>
    public string TypeLabel => Types.Count == 0 ? "null" : string.Join(" | ", Types);

    /// <summary>Gets whether some sampled documents lack the field or hold <c>null</c>.</summary>
    public bool IsOptional => Count < Sampled || Types.Contains("null");
}

/// <summary>
/// Infers a collection's fields from a sample of its documents, the way a
/// database IDE shows a schemaless collection: the fields at a path with the
/// types seen, an <c>array</c> of documents described by its elements' fields.
/// </summary>
public static class MongoFields
{
    /// <summary>The number of documents sampled to infer a collection's fields.</summary>
    public const int SampleSize = 100;

    /// <summary>Reads a sample of a collection's documents: random for a collection, the first ones for a view.</summary>
    /// <param name="database">The database.</param>
    /// <param name="collection">The collection or view.</param>
    /// <param name="isView">Whether it is a view (sampled from its start, so its pipeline runs once).</param>
    /// <param name="size">The sample size.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The documents.</returns>
    public static async Task<List<BsonDocument>> SampleAsync(IMongoDatabase database, string collection, bool isView, int size, CancellationToken cancellationToken)
    {
        var target = database.GetCollection<BsonDocument>(collection);
        var cursor = isView
            ? await target.FindAsync(FilterDefinition<BsonDocument>.Empty, new FindOptions<BsonDocument> { Limit = size }, cancellationToken).ConfigureAwait(false)
            : await target.AggregateAsync(
                PipelineDefinition<BsonDocument, BsonDocument>.Create([new BsonDocument("$sample", new BsonDocument("size", size))]),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        return await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Infers the fields at a path of the documents.</summary>
    /// <param name="documents">The sampled documents.</param>
    /// <param name="path">The dotted path of an embedded document (or array of documents), or <c>null</c> for the top level.</param>
    /// <returns>The fields, <c>_id</c> first, then in order of first appearance.</returns>
    public static IReadOnlyList<MongoField> Infer(IReadOnlyList<BsonDocument> documents, string? path = null)
    {
        var scopes = path is null ? documents : documents.SelectMany(d => Scopes(d, path.Split('.'), 0)).ToList();
        var order = new List<string>();
        var types = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var children = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            foreach (var element in scope)
            {
                if (!types.TryGetValue(element.Name, out var seen))
                {
                    seen = new Dictionary<string, int>(StringComparer.Ordinal);
                    types[element.Name] = seen;
                    order.Add(element.Name);
                }

                var type = TypeOf(element.Value);
                seen[type] = seen.GetValueOrDefault(type) + 1;
                counts[element.Name] = counts.GetValueOrDefault(element.Name) + 1;
                if (element.Value is BsonDocument || element.Value is BsonArray array && array.Any(v => v is BsonDocument))
                {
                    children.Add(element.Name);
                }
            }
        }

        return order
            .OrderBy(name => name == "_id" ? 0 : 1)
            .Select(name => new MongoField(
                path is null ? name : $"{path}.{name}",
                name,
                types[name].OrderByDescending(t => t.Value).Select(t => t.Key).ToList(),
                counts[name],
                scopes.Count,
                children.Contains(name)))
            .ToList();
    }

    /// <summary>Lists the fields of documents with their nested fields, as dotted paths, down to a depth.</summary>
    /// <param name="documents">The sampled documents.</param>
    /// <param name="depth">The deepest nesting level to include (1 is the top level).</param>
    /// <returns>The fields, each followed by its nested fields.</returns>
    public static IReadOnlyList<MongoField> Flatten(IReadOnlyList<BsonDocument> documents, int depth = 3)
    {
        var fields = new List<MongoField>();
        void Walk(string? path, int level)
        {
            foreach (var field in Infer(documents, path))
            {
                fields.Add(field);
                if (field.HasChildren && level < depth)
                {
                    Walk(field.Path, level + 1);
                }
            }
        }

        Walk(null, 1);
        return fields;
    }

    /// <summary>Maps a field's dominant type to a schema data type.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The data type.</returns>
    public static ColumnTypeOptions TypeOf(MongoField field)
    {
        var types = field.Types.Where(t => t != "null").ToList();
        return types.Count != 1 ? ColumnTypeOptions.Of(DbDataType.Json) : types[0] switch
        {
            "string" => ColumnTypeOptions.Of(DbDataType.Text),
            "int" => ColumnTypeOptions.Of(DbDataType.Integer),
            "long" => ColumnTypeOptions.Of(DbDataType.BigInt),
            "double" => ColumnTypeOptions.Of(DbDataType.Double),
            "decimal" => ColumnTypeOptions.Of(DbDataType.Decimal),
            "bool" => ColumnTypeOptions.Of(DbDataType.Boolean),
            "date" => ColumnTypeOptions.Of(DbDataType.TimestampTz),
            "objectId" => new ColumnTypeOptions { DataType = DbDataType.Char, Length = 24 },
            "uuid" => ColumnTypeOptions.Of(DbDataType.Uuid),
            "binData" => ColumnTypeOptions.Of(DbDataType.Blob),
            _ => ColumnTypeOptions.Of(DbDataType.Json),
        };
    }

    // "array" is refined by its elements: array<string>, array<object>, array<mixed>.
    private static string TypeOf(BsonValue value)
    {
        if (value is not BsonArray array)
        {
            return MongoValues.TypeName(value);
        }

        var elements = array.Select(MongoValues.TypeName).Distinct().ToList();
        return elements.Count switch
        {
            0 => "array",
            1 => $"array<{elements[0]}>",
            _ => "array<mixed>",
        };
    }

    private static IEnumerable<BsonDocument> Scopes(BsonValue value, string[] path, int index)
    {
        if (index == path.Length)
        {
            if (value is BsonDocument document)
            {
                yield return document;
            }
            else if (value is BsonArray array)
            {
                foreach (var item in array.OfType<BsonDocument>())
                {
                    yield return item;
                }
            }

            yield break;
        }

        IEnumerable<BsonValue> next = value switch
        {
            BsonDocument document when document.TryGetValue(path[index], out var child) => [child],
            BsonArray array => array.OfType<BsonDocument>().Where(d => d.Contains(path[index])).Select(d => d[path[index]]),
            _ => [],
        };
        foreach (var child in next)
        {
            foreach (var scope in Scopes(child, path, index + 1))
            {
                yield return scope;
            }
        }
    }
}

/// <summary>
/// Discovery and schema inference for a MongoDB database: its databases, its
/// collections and views, and a <see cref="Table"/> per collection inferred
/// from sampled documents (with <c>_id</c> as the primary key and the
/// collection's indexes), so a collection can feed the same tools as a table.
/// MongoDB has no schemas.
/// </summary>
public sealed class MongoSchemaProvider : IDatabaseExplorer, IDatabaseIntrospector
{
    private readonly MongoConnection _connection;

    /// <summary>Creates the provider.</summary>
    /// <param name="connection">The connection.</param>
    public MongoSchemaProvider(MongoConnection connection) => _connection = connection;

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        var names = await (await _connection.Client.ListDatabaseNamesAsync(new ListDatabaseNamesOptions { AuthorizedDatabases = true }, cancellationToken).ConfigureAwait(false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return (IReadOnlyList<string>)names.Order(StringComparer.Ordinal).ToList();
    });

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetSchemasAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<TableRef>> GetTablesAsync(string? schema = null, CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        var collections = await ListAsync(_connection.Database, cancellationToken).ConfigureAwait(false);
        return (IReadOnlyList<TableRef>)collections.Select(c => new TableRef(c.Name, null, c.IsView ? TableKind.View : TableKind.Table)).ToList();
    });

    /// <inheritdoc />
    public Task<DatabaseSchema> IntrospectAsync(string? schema = null, CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        var tables = new List<Table>();
        foreach (var collection in (await ListAsync(_connection.Database, cancellationToken).ConfigureAwait(false)).Where(c => !c.IsView))
        {
            tables.Add(await InferAsync(collection.Name, isView: false, cancellationToken).ConfigureAwait(false));
        }

        return new DatabaseSchema { Name = _connection.DatabaseName, Tables = tables };
    });

    /// <inheritdoc />
    public Task<Table> IntrospectTableAsync(TableRef table, CancellationToken cancellationToken = default) =>
        GuardAsync(() => InferAsync(table.Name, table.Kind == TableKind.View, cancellationToken));

    /// <summary>Lists a database's collections and views, without the <c>system.*</c> collections.</summary>
    /// <param name="database">The database.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The collections by name, with whether each is a view and its options.</returns>
    public static async Task<IReadOnlyList<(string Name, bool IsView, BsonDocument Info)>> ListAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var infos = await (await database.ListCollectionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return infos
            .Select(i => (Name: i["name"].AsString, IsView: i.GetValue("type", "collection").AsString == "view", Info: i))
            .Where(c => !c.Name.StartsWith("system.", StringComparison.Ordinal))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
    }

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

    private async Task<Table> InferAsync(string name, bool isView, CancellationToken cancellationToken)
    {
        var sample = await MongoFields.SampleAsync(_connection.Database, name, isView, MongoFields.SampleSize, cancellationToken).ConfigureAwait(false);
        var fields = MongoFields.Infer(sample);
        var indexes = isView ? [] : await (await _connection.Collection(name).Indexes.ListAsync(cancellationToken).ConfigureAwait(false)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new Table
        {
            Name = name,
            Columns = fields.Select(f => new Column
            {
                Name = f.Name,
                Type = MongoFields.TypeOf(f),
                Nullability = f.Name != "_id" && f.IsOptional ? Nullability.Nullable : Nullability.NotNull,
            }).ToList(),
            PrimaryKey = fields.Any(f => f.Name == "_id") ? new PrimaryKey { Name = "_id_", ColumnNames = ["_id"] } : null,
            Indexes = indexes
                .Where(i => i.GetValue("name", "_id_").AsString != "_id_")
                .Select(i => new Bengine.Models.Index
                {
                    Name = i["name"].AsString,
                    Type = i.GetValue("unique", false).ToBoolean() ? IndexType.Unique : IndexType.NonUnique,
                    Columns = i["key"].AsBsonDocument
                        .Select(k => k.Value.IsNumeric && k.Value.ToDouble() < 0 ? IndexColumn.Descending(k.Name) : IndexColumn.Ascending(k.Name))
                        .ToList(),
                    Filter = i.TryGetValue("partialFilterExpression", out var filter) ? MongoValues.Shell(filter) : null,
                })
                .ToList(),
        };
    }
}

/// <summary>Reads a MongoDB database's metadata snapshot: its collections and views with fields inferred from a small sample of each.</summary>
public static class MongoCatalog
{
    private const int SamplePerCollection = 20;
    private const int MaxCollections = 300;

    /// <summary>Reads the catalog.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The catalog: one relation per collection or view, <c>_id</c> as the key, nested fields as dotted names.</returns>
    /// <exception cref="IntrospectionException">The server refused.</exception>
    public static async Task<DatabaseCatalog> ReadAsync(MongoConnection connection, CancellationToken cancellationToken = default)
    {
        try
        {
            var collections = (await MongoSchemaProvider.ListAsync(connection.Database, cancellationToken).ConfigureAwait(false)).Take(MaxCollections).ToList();
            var relations = new CatalogRelation[collections.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, collections.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                async (i, ct) =>
                {
                    var (name, isView, _) = collections[i];
                    List<BsonDocument> sample;
                    try
                    {
                        sample = await MongoFields.SampleAsync(connection.Database, name, isView, SamplePerCollection, ct).ConfigureAwait(false);
                    }
                    catch (MongoException)
                    {
                        // A view whose pipeline fails, or a collection the user may not read, still lists.
                        sample = [];
                    }

                    var columns = MongoFields.Flatten(sample)
                        .Select(f => new CatalogColumn(f.Path, f.TypeLabel, f.Path != "_id" && f.IsOptional, f.Path == "_id"))
                        .ToList();
                    relations[i] = new CatalogRelation(null, name, isView ? TableKind.View : TableKind.Table, columns);
                }).ConfigureAwait(false);
            return new DatabaseCatalog(DatabaseProvider.MongoDb, connection.DatabaseName, null, [], relations, [], []);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            throw new IntrospectionException(MongoConnection.Message(ex), ex);
        }
    }
}
