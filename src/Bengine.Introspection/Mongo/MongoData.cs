namespace Bengine.Introspection.Mongo;

using System.Text.RegularExpressions;
using Bengine.Enums;
using Bengine.Introspection.Abstractions;
using Bengine.Introspection.Model;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Reads a collection's documents a page at a time for the data editor. The
/// editor's <c>WHERE</c> box holds a query filter and its <c>ORDER BY</c> box a
/// sort document, both in shell syntax (<c>{age: {$gt: 30}}</c>, <c>{name: 1}</c>;
/// the outer braces may be left out), combined with the structured filters and
/// sort terms. Documents are flattened by <see cref="MongoResults"/>.
/// </summary>
public sealed class MongoDataReader : ITableDataReader
{
    private readonly MongoConnection _connection;

    /// <summary>Creates the reader.</summary>
    /// <param name="connection">The connection.</param>
    public MongoDataReader(MongoConnection connection) => _connection = connection;

    /// <inheritdoc />
    public async Task<DataPage> QueryAsync(TableRef table, DataQuery query, CancellationToken cancellationToken = default)
    {
        var filter = Filter(query);
        var sort = Sort(query);
        var collection = _connection.Collection(table.Name);
        try
        {
            var options = new FindOptions<BsonDocument> { Skip = query.Offset, Limit = query.PageSize + 1, Sort = sort.ElementCount > 0 ? sort : null };
            var documents = await (await collection.FindAsync(filter, options, cancellationToken).ConfigureAwait(false)).ToListAsync(cancellationToken).ConfigureAwait(false);
            long? total = !query.IncludeTotalCount ? null
                : filter.ElementCount == 0 && table.Kind == TableKind.Table ? await collection.EstimatedDocumentCountAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
                : await collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            var hasMore = documents.Count > query.PageSize;
            if (hasMore)
            {
                documents.RemoveAt(documents.Count - 1);
            }

            var (columns, types, rows) = MongoResults.Flatten(documents);
            if (columns.Count == 0)
            {
                // An empty page still has the key column, so a document can be added.
                (columns, types) = (["_id"], ["objectId"]);
            }

            return new DataPage
            {
                Columns = columns,
                ColumnTypes = types,
                Rows = rows,
                Page = query.Page,
                PageSize = query.PageSize,
                TotalCount = total,
                HasMore = hasMore,
                Sql = $"{MongoShell.Collection(table.Name)}.find({MongoValues.Shell(filter)})" + (sort.ElementCount > 0 ? $".sort({MongoValues.Shell(sort)})" : string.Empty),
            };
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            throw new IntrospectionException(MongoConnection.Message(ex), ex);
        }
    }

    /// <summary>Builds the filter of a page request: the typed filter document and the structured conditions.</summary>
    /// <param name="query">The request.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="IntrospectionException">The typed filter is not a document.</exception>
    public static BsonDocument Filter(DataQuery query)
    {
        var parts = new List<BsonDocument>();
        if (Document(query.WhereSql, "filter") is { ElementCount: > 0 } typed)
        {
            parts.Add(typed);
        }

        parts.AddRange(query.Filters.Select(Condition));
        return parts.Count switch
        {
            0 => [],
            1 => parts[0],
            _ => new BsonDocument("$and", new BsonArray(parts)),
        };
    }

    /// <summary>Builds the sort of a page request: the typed sort document, else the sort terms.</summary>
    /// <param name="query">The request.</param>
    /// <returns>The sort document (empty for natural order).</returns>
    /// <exception cref="IntrospectionException">The typed sort is not a document.</exception>
    public static BsonDocument Sort(DataQuery query) =>
        Document(query.OrderBySql, "sort") is { ElementCount: > 0 } typed
            ? typed
            : new BsonDocument(query.Sort.Select(s => new BsonElement(s.Column, s.Direction == Bengine.Enums.SortDirection.Descending ? -1 : 1)));

    // A typed document; "a: 1" is read as "{a: 1}".
    private static BsonDocument? Document(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        var value = MongoValues.ParseValue(trimmed.StartsWith('{') ? trimmed : "{" + trimmed + "}");
        return value as BsonDocument ?? throw new IntrospectionException($"The {what} must be a document, for example {{name: 1}}.");
    }

    private static BsonDocument Condition(FilterCondition condition)
    {
        var value = MongoValues.ToBson(condition.Value);
        var text = condition.Value?.ToString() ?? string.Empty;
        BsonValue test = condition.Operator switch
        {
            FilterOperator.Equals => value,
            FilterOperator.NotEquals => new BsonDocument("$ne", value),
            FilterOperator.LessThan => new BsonDocument("$lt", value),
            FilterOperator.LessThanOrEqual => new BsonDocument("$lte", value),
            FilterOperator.GreaterThan => new BsonDocument("$gt", value),
            FilterOperator.GreaterThanOrEqual => new BsonDocument("$gte", value),
            FilterOperator.Contains => new BsonRegularExpression(Regex.Escape(text), "i"),
            FilterOperator.StartsWith => new BsonRegularExpression("^" + Regex.Escape(text), "i"),
            FilterOperator.EndsWith => new BsonRegularExpression(Regex.Escape(text) + "$", "i"),
            FilterOperator.In => new BsonDocument("$in", new BsonArray(condition.Values.Select(MongoValues.ToBson))),
            FilterOperator.IsNull => BsonNull.Value,
            FilterOperator.IsNotNull => new BsonDocument("$ne", BsonNull.Value),
            _ => throw new IntrospectionException($"The filter operator {condition.Operator} is not supported."),
        };
        return new BsonDocument(condition.Column, test);
    }
}

/// <summary>
/// Writes the data editor's changes to a collection. A row's key is its
/// <c>_id</c>; an update sets the edited fields (<c>$set</c>) and removes
/// those set to DEFAULT (<c>$unset</c>); an insert leaves out DEFAULT fields
/// and a missing <c>_id</c>, which the server generates. On a replica set or
/// sharded cluster a change set is applied in one transaction; a standalone
/// server has none, so changes apply in order and stop at the first failure.
/// </summary>
public sealed class MongoDataWriter : ITableDataWriter
{
    private readonly MongoConnection _connection;

    /// <summary>Creates the writer.</summary>
    /// <param name="connection">The connection.</param>
    public MongoDataWriter(MongoConnection connection) => _connection = connection;

    /// <summary>Renders changes as the shell commands they amount to, for review.</summary>
    /// <param name="table">The collection.</param>
    /// <param name="changes">The changes.</param>
    /// <returns>One command per change.</returns>
    public static IReadOnlyList<string> Preview(TableRef table, IReadOnlyList<RowChange> changes)
    {
        var target = MongoShell.Collection(table.Name);
        return changes.Select(change => change.Kind switch
        {
            RowChangeKind.Insert => $"{target}.insertOne({MongoValues.Shell(Document(change.Values))});",
            RowChangeKind.Update => $"{target}.updateOne({MongoValues.Shell(Key(change.Key))}, {MongoValues.Shell(Update(change.Values))});",
            _ => $"{target}.deleteOne({MongoValues.Shell(Key(change.Key))});",
        }).ToList();
    }

    /// <summary>Builds the document an insert writes: DEFAULT fields and a <c>null</c> <c>_id</c> are left to the server.</summary>
    /// <param name="values">The field values.</param>
    /// <returns>The document.</returns>
    public static BsonDocument Document(IReadOnlyDictionary<string, object?> values)
    {
        var document = new BsonDocument();
        foreach (var (name, value) in values)
        {
            if (value is SqlDefault || (name == "_id" && value is null))
            {
                continue;
            }

            document[name] = MongoValues.ToBson(value);
        }

        return document;
    }

    /// <inheritdoc />
    public Task<int> InsertAsync(TableRef table, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        GuardAsync(async () =>
        {
            await _connection.Collection(table.Name).InsertOneAsync(Document(values), cancellationToken: cancellationToken).ConfigureAwait(false);
            return 1;
        });

    /// <inheritdoc />
    public Task<int> UpdateAsync(TableRef table, IReadOnlyDictionary<string, object?> key, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        GuardAsync(async () =>
        {
            var result = await _connection.Collection(table.Name).UpdateManyAsync(Key(key), Update(values), cancellationToken: cancellationToken).ConfigureAwait(false);
            return (int)result.MatchedCount;
        });

    /// <inheritdoc />
    public Task<int> DeleteAsync(TableRef table, IReadOnlyDictionary<string, object?> key, CancellationToken cancellationToken = default) =>
        GuardAsync(async () => (int)(await _connection.Collection(table.Name).DeleteManyAsync(Key(key), cancellationToken).ConfigureAwait(false)).DeletedCount);

    /// <inheritdoc />
    public Task<int> InsertManyAsync(TableRef table, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) =>
        GuardAsync(async () =>
        {
            if (rows.Count > 0)
            {
                await _connection.Collection(table.Name).InsertManyAsync(rows.Select(Document), cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return rows.Count;
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<int>> ApplyAsync(TableRef table, IReadOnlyList<RowChange> changes, CancellationToken cancellationToken = default) =>
        GuardAsync(async () =>
        {
            var collection = _connection.Collection(table.Name);
            if (!await SupportsTransactionsAsync(cancellationToken).ConfigureAwait(false))
            {
                return await ApplyAllAsync(collection, null, changes, cancellationToken).ConfigureAwait(false);
            }

            using var session = await _connection.Client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            session.StartTransaction();
            try
            {
                var counts = await ApplyAllAsync(collection, session, changes, cancellationToken).ConfigureAwait(false);
                await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
                return counts;
            }
            catch
            {
                if (session.IsInTransaction)
                {
                    await session.AbortTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
        });

    private static BsonDocument Key(IReadOnlyDictionary<string, object?> key)
    {
        if (key.Count == 0)
        {
            throw new IntrospectionException("A change needs a key to find its document.");
        }

        // _id identifies a document on its own.
        return key.TryGetValue("_id", out var id) && id is not null
            ? new BsonDocument("_id", MongoValues.ToBson(id))
            : new BsonDocument(key.Select(k => new BsonElement(k.Key, MongoValues.ToBson(k.Value))));
    }

    private static BsonDocument Update(IReadOnlyDictionary<string, object?> values)
    {
        var set = new BsonDocument(values.Where(v => v.Value is not SqlDefault).Select(v => new BsonElement(v.Key, MongoValues.ToBson(v.Value))));
        var unset = new BsonDocument(values.Where(v => v.Value is SqlDefault).Select(v => new BsonElement(v.Key, string.Empty)));
        var update = new BsonDocument();
        if (set.ElementCount > 0)
        {
            update["$set"] = set;
        }

        if (unset.ElementCount > 0)
        {
            update["$unset"] = unset;
        }

        return update.ElementCount > 0 ? update : throw new IntrospectionException("An update needs at least one field to change.");
    }

    private static async Task<IReadOnlyList<int>> ApplyAllAsync(IMongoCollection<BsonDocument> collection, IClientSessionHandle? session, IReadOnlyList<RowChange> changes, CancellationToken ct)
    {
        var counts = new List<int>();
        foreach (var change in changes)
        {
            long count;
            try
            {
                count = change.Kind switch
                {
                    RowChangeKind.Insert => await InsertOneAsync(collection, session, Document(change.Values), ct).ConfigureAwait(false),
                    RowChangeKind.Update => session is null
                        ? (await collection.UpdateOneAsync(Key(change.Key), Update(change.Values), cancellationToken: ct).ConfigureAwait(false)).MatchedCount
                        : (await collection.UpdateOneAsync(session, Key(change.Key), Update(change.Values), cancellationToken: ct).ConfigureAwait(false)).MatchedCount,
                    _ => session is null
                        ? (await collection.DeleteOneAsync(Key(change.Key), ct).ConfigureAwait(false)).DeletedCount
                        : (await collection.DeleteOneAsync(session, Key(change.Key), cancellationToken: ct).ConfigureAwait(false)).DeletedCount,
                };
            }
            catch (Exception ex) when (session is null && counts.Count > 0 && ex is MongoException)
            {
                throw new IntrospectionException(
                    $"{MongoConnection.Message(ex)} — {counts.Count} earlier change(s) were saved: a standalone MongoDB server has no transactions to roll them back.", ex);
            }

            if (count != 1)
            {
                var saved = session is null && counts.Count > 0 ? $" {counts.Count} earlier change(s) were saved (no transactions on a standalone server)." : string.Empty;
                throw new IntrospectionException($"Change {counts.Count + 1} found {count} documents instead of one; it was not applied — reload and try again.{saved}");
            }

            counts.Add(1);
        }

        return counts;
    }

    private static async Task<long> InsertOneAsync(IMongoCollection<BsonDocument> collection, IClientSessionHandle? session, BsonDocument document, CancellationToken ct)
    {
        if (session is null)
        {
            await collection.InsertOneAsync(document, cancellationToken: ct).ConfigureAwait(false);
        }
        else
        {
            await collection.InsertOneAsync(session, document, cancellationToken: ct).ConfigureAwait(false);
        }

        return 1;
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

    // Transactions need a replica set member or mongos.
    private async Task<bool> SupportsTransactionsAsync(CancellationToken ct)
    {
        var hello = await _connection.Admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: ct).ConfigureAwait(false);
        return hello.Contains("setName") || hello.GetValue("msg", BsonNull.Value) is BsonString { Value: "isdbgrid" };
    }
}
