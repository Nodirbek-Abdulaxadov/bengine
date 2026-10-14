namespace Bengine.Introspection.Mongo;

using System.Diagnostics;
using Bengine.Introspection.Abstractions;
using Bengine.Introspection.Model;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// The MongoDB query console: runs one shell command (see
/// <see cref="MongoShell"/>) — <c>show dbs</c>, <c>db.coll.find(…)</c> with its
/// cursor methods, <c>aggregate</c>, counts and <c>distinct</c>, the write and
/// index methods, database methods, and command documents — and returns
/// documents as a result set or a write's document count. A read-only
/// connection refuses commands that can write, since MongoDB has no read-only
/// sessions.
/// </summary>
public sealed class MongoQueryExecutor : IQueryExecutor
{
    private readonly MongoConnection _connection;
    private readonly bool _readOnly;

    /// <summary>Creates the console over a connection.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="readOnly">Whether commands that can write are refused.</param>
    public MongoQueryExecutor(MongoConnection connection, bool readOnly = false)
    {
        _connection = connection;
        _readOnly = readOnly;
    }

    /// <inheritdoc />
    public async Task<QueryResult> ExecuteAsync(string sql, int maxRows = 1000, CancellationToken cancellationToken = default) =>
        (await ExecuteAllAsync(sql, maxRows, cancellationToken).ConfigureAwait(false))[0];

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueryResult>> ExecuteAllAsync(string sql, int maxRows = 1000, CancellationToken cancellationToken = default)
    {
        var command = MongoShell.Parse(sql);
        if (_readOnly && MongoShell.Classify(command).CanModify)
        {
            throw new IntrospectionException($"The connection is read-only: {MongoShell.Classify(command).Verb} can write, so it was not run.");
        }

        var clock = Stopwatch.StartNew();
        try
        {
            var result = await RunAsync(command, Math.Max(1, maxRows), cancellationToken).ConfigureAwait(false);
            return [result with { ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds }];
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or InvalidOperationException or FormatException)
        {
            throw new IntrospectionException(MongoConnection.Message(ex), ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>MongoDB has no SQL to stream; whole-collection work reads documents directly.</remarks>
    public IAsyncEnumerable<object?[]> StreamAsync(string sql, CancellationToken cancellationToken = default) =>
        throw new IntrospectionException("Streaming SQL rows is not available for MongoDB.");

    /// <summary>
    /// Builds the command an explainable shell command runs — <c>find</c>,
    /// <c>aggregate</c>, <c>count</c>, <c>distinct</c>, updates, and deletes —
    /// for the <c>explain</c> command. An <c>explain()</c> in the chain is ignored.
    /// </summary>
    /// <param name="command">The shell command.</param>
    /// <returns>The command document to explain.</returns>
    /// <exception cref="IntrospectionException">The command cannot be explained.</exception>
    public static BsonDocument ExplainTarget(ShellCommand command)
    {
        var calls = command.Calls.Where(c => c.Name != "explain").ToList();
        if (command.Target != ShellTarget.Collection || calls.Count == 0)
        {
            throw new IntrospectionException("Only collection commands can be explained: find, aggregate, count, distinct, update, and delete.");
        }

        var method = calls[0];
        var collection = command.Collection!;
        switch (method.Name)
        {
            case "find" or "findOne":
                var find = new BsonDocument { { "find", collection }, { "filter", method.Document(0) } };
                if (method.OptionalDocument(1) is { } projection)
                {
                    find["projection"] = projection;
                }

                if (method.Name == "findOne")
                {
                    find["limit"] = 1;
                }

                foreach (var call in calls.Skip(1))
                {
                    switch (call.Name)
                    {
                        case "sort":
                            find["sort"] = call.Document(0);
                            break;
                        case "limit":
                            find["limit"] = Math.Abs(call.Number(0));
                            break;
                        case "skip":
                            find["skip"] = call.Number(0);
                            break;
                        case "projection" or "project":
                            find["projection"] = call.Document(0);
                            break;
                        case "hint":
                            find["hint"] = call.Argument(0) ?? BsonNull.Value;
                            break;
                        case "collation":
                            find["collation"] = call.Document(0);
                            break;
                        case "maxTimeMS":
                            find["maxTimeMS"] = call.Number(0);
                            break;
                    }
                }

                return find;
            case "aggregate":
                var aggregate = new BsonDocument { { "aggregate", collection }, { "pipeline", new BsonArray(Stages(method)) }, { "cursor", new BsonDocument() } };
                if (method.Argument(0) is BsonArray && method.OptionalDocument(1) is { } options)
                {
                    aggregate.Merge(options, overwriteExistingElements: false);
                }

                return aggregate;
            case "count" or "countDocuments":
                return new BsonDocument { { "count", collection }, { "query", method.Document(0) } };
            case "distinct":
                return new BsonDocument { { "distinct", collection }, { "key", method.String(0) }, { "query", method.Document(1) } };
            case "updateOne" or "updateMany" or "replaceOne" or "update":
                var many = method.Name == "updateMany" || method.Name == "update" && method.Document(2).GetValue("multi", false).ToBoolean();
                return new BsonDocument
                {
                    { "update", collection },
                    {
                        "updates", new BsonArray
                        {
                            new BsonDocument
                            {
                                { "q", method.Document(0) },
                                { "u", method.Argument(1) ?? new BsonDocument() },
                                { "multi", many },
                                { "upsert", method.Document(2).GetValue("upsert", false).ToBoolean() },
                            },
                        }
                    },
                };
            case "deleteOne" or "deleteMany" or "remove":
                return new BsonDocument
                {
                    { "delete", collection },
                    { "deletes", new BsonArray { new BsonDocument { { "q", method.Document(0) }, { "limit", method.Name == "deleteOne" ? 1 : 0 } } } },
                };
            default:
                throw new IntrospectionException($"{method.Name}() cannot be explained; explain find, aggregate, count, distinct, update, or delete.");
        }
    }

    /// <summary>Reads the verbosity an <c>explain(…)</c> call asks for.</summary>
    /// <param name="argument">The call's argument.</param>
    /// <returns><c>queryPlanner</c>, <c>executionStats</c>, or <c>allPlansExecution</c>.</returns>
    public static string Verbosity(BsonValue? argument) => argument switch
    {
        BsonString { Value: var mode } => mode,
        BsonBoolean { Value: true } => "allPlansExecution",
        _ => "queryPlanner",
    };

    private static List<BsonDocument> Stages(ShellCall method) => method.Argument(0) switch
    {
        null => [],
        BsonArray stages => stages.Select(s => s is BsonDocument stage ? stage : throw new IntrospectionException("aggregate(): every pipeline stage must be a document.")).ToList(),

        // The old form passes the stages as separate arguments.
        BsonDocument => method.Arguments.Select(s => s as BsonDocument ?? throw new IntrospectionException("aggregate(): every pipeline stage must be a document.")).ToList(),
        _ => throw new IntrospectionException("aggregate(): the pipeline must be an array of stages."),
    };

    private static async Task<List<BsonDocument>> TakeAsync(IAsyncCursor<BsonDocument> cursor, int maxRows, CancellationToken ct)
    {
        var documents = new List<BsonDocument>();
        using (cursor)
        {
            while (documents.Count <= maxRows && await cursor.MoveNextAsync(ct).ConfigureAwait(false))
            {
                documents.AddRange(cursor.Current.Take(maxRows + 1 - documents.Count));
            }
        }

        return documents;
    }

    private static QueryResult Page(List<BsonDocument> documents, int maxRows) =>
        MongoResults.Documents(documents.Count > maxRows ? documents[..maxRows] : documents, documents.Count > maxRows);

    private static UpdateDefinition<BsonDocument> Update(ShellCall method, int index) => method.Argument(index) switch
    {
        BsonDocument update => new BsonDocumentUpdateDefinition<BsonDocument>(update),
        BsonArray stages => Builders<BsonDocument>.Update.Pipeline(PipelineDefinition<BsonDocument, BsonDocument>.Create(
            stages.Select(s => s as BsonDocument ?? throw new IntrospectionException($"{method.Name}(): every update pipeline stage must be a document.")))),
        _ => throw new IntrospectionException($"{method.Name}(): the update must be a document ({{$set: {{…}}}}) or a pipeline."),
    };

    private static Collation? Collation(BsonDocument options) =>
        options.TryGetValue("collation", out var collation) && collation is BsonDocument document ? MongoDB.Driver.Collation.FromBsonDocument(document) : null;

    private static string IndexName(BsonDocument keys) =>
        string.Join("_", keys.Select(k => $"{k.Name}_{(k.Value.IsString ? k.Value.AsString : k.Value.ToString())}"));

    private IMongoDatabase Database(ShellCommand command) => command.Database is { } name ? _connection.Client.GetDatabase(name) : _connection.Database;

    private Task<QueryResult> RunAsync(ShellCommand command, int maxRows, CancellationToken ct) => command.Target switch
    {
        ShellTarget.Show => ShowAsync(command.Word!, maxRows, ct),
        ShellTarget.Use => throw new IntrospectionException($"Choose the database in the console's database list instead of 'use {command.Word}'."),
        ShellTarget.Command => CommandAsync(_connection.Database, command.Command!, maxRows, ct),
        ShellTarget.Database => DatabaseMethodAsync(command, maxRows, ct),
        _ => CollectionMethodAsync(command, maxRows, ct),
    };

    // Runs a command; a cursor reply shows its first batch.
    private async Task<QueryResult> CommandAsync(IMongoDatabase database, BsonDocument command, int maxRows, CancellationToken ct)
    {
        var reply = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: ct).ConfigureAwait(false);
        if (reply.TryGetValue("cursor", out var cursor) && cursor is BsonDocument { } batch && batch.TryGetValue("firstBatch", out var first) && first is BsonArray documents)
        {
            var id = batch.GetValue("id", 0L).ToInt64();
            if (id != 0 && batch.GetValue("ns", BsonNull.Value) is BsonString { Value: var ns } && ns.IndexOf('.') is var dot and > 0)
            {
                // Only the first batch is shown; the rest of the cursor is released.
                await _connection.Client.GetDatabase(ns[..dot])
                    .RunCommandAsync<BsonDocument>(new BsonDocument { { "killCursors", ns[(dot + 1)..] }, { "cursors", new BsonArray { id } } }, cancellationToken: ct)
                    .ConfigureAwait(false);
            }

            var list = documents.OfType<BsonDocument>().ToList();
            return MongoResults.Documents(list.Count > maxRows ? list[..maxRows] : list, id != 0 || list.Count > maxRows);
        }

        return MongoResults.Documents([reply]);
    }

    private async Task<BsonDocument> RunAsync(IMongoDatabase database, BsonDocument command, CancellationToken ct) =>
        await database.RunCommandAsync<BsonDocument>(command, cancellationToken: ct).ConfigureAwait(false);

    private async Task<QueryResult> ShowAsync(string what, int maxRows, CancellationToken ct)
    {
        switch (what)
        {
            case "dbs" or "databases":
                var reply = await RunAsync(_connection.Admin, new BsonDocument { { "listDatabases", 1 }, { "authorizedDatabases", true } }, ct).ConfigureAwait(false);
                return MongoResults.Documents(reply["databases"].AsBsonArray.OfType<BsonDocument>().ToList());
            case "collections" or "tables":
                var collections = await TakeAsync(await _connection.Database.ListCollectionsAsync(cancellationToken: ct).ConfigureAwait(false), maxRows, ct).ConfigureAwait(false);
                return MongoResults.Documents(collections
                    .Where(c => !c["name"].AsString.StartsWith("system.", StringComparison.Ordinal))
                    .OrderBy(c => c["name"].AsString, StringComparer.Ordinal)
                    .Select(c => new BsonDocument { { "name", c["name"] }, { "type", c.GetValue("type", "collection") } })
                    .ToList());
            case "users":
                return MongoResults.Documents((await RunAsync(_connection.Database, new BsonDocument("usersInfo", 1), ct).ConfigureAwait(false))["users"].AsBsonArray.OfType<BsonDocument>().ToList());
            case "roles":
                return MongoResults.Documents((await RunAsync(_connection.Database, new BsonDocument("rolesInfo", 1), ct).ConfigureAwait(false))["roles"].AsBsonArray.OfType<BsonDocument>().ToList());
            default:
                throw new IntrospectionException($"'show {what}' is not supported; use show dbs, show collections, show users, or show roles.");
        }
    }

    private async Task<QueryResult> DatabaseMethodAsync(ShellCommand command, int maxRows, CancellationToken ct)
    {
        var database = Database(command);
        var method = command.Method!;
        switch (method.Name)
        {
            case "runCommand":
            case "adminCommand":
                var target = method.Name == "adminCommand" ? _connection.Admin : database;
                var commandDocument = method.Argument(0) is BsonString { Value: var name } ? new BsonDocument(name, 1) : method.Document(0);
                return await CommandAsync(target, commandDocument, maxRows, ct).ConfigureAwait(false);
            case "getCollectionNames":
                var names = await (await database.ListCollectionNamesAsync(cancellationToken: ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false);
                return MongoResults.Values("name", names.Order(StringComparer.Ordinal).Select(n => (BsonValue)n).ToList());
            case "getCollectionInfos":
                var options = new ListCollectionsOptions { Filter = method.Document(0) };
                return Page(await TakeAsync(await database.ListCollectionsAsync(options, ct).ConfigureAwait(false), maxRows, ct).ConfigureAwait(false), maxRows);
            case "createCollection":
                var create = new BsonDocument("create", method.String(0));
                create.Merge(method.Document(1), overwriteExistingElements: false);
                await RunAsync(database, create, ct).ConfigureAwait(false);
                return MongoResults.Affected(-1);
            case "createView":
                var view = new BsonDocument { { "create", method.String(0) }, { "viewOn", method.String(1) }, { "pipeline", method.Argument(2) as BsonArray ?? [] } };
                view.Merge(method.Document(3), overwriteExistingElements: false);
                await RunAsync(database, view, ct).ConfigureAwait(false);
                return MongoResults.Affected(-1);
            case "dropDatabase":
                await RunAsync(database, new BsonDocument("dropDatabase", 1), ct).ConfigureAwait(false);
                return MongoResults.Affected(-1);
            case "stats":
                var stats = new BsonDocument("dbStats", 1);
                if (method.Argument(0) is { IsNumeric: true } scale)
                {
                    stats["scale"] = scale;
                }

                return MongoResults.Documents([await RunAsync(database, stats, ct).ConfigureAwait(false)]);
            case "version":
                return MongoResults.Value("version", (await RunAsync(_connection.Admin, new BsonDocument("buildInfo", 1), ct).ConfigureAwait(false))["version"]);
            case "serverStatus" or "hostInfo" or "buildInfo" or "serverBuildInfo" or "hello" or "isMaster" or "listCommands":
                var commandName = method.Name == "serverBuildInfo" ? "buildInfo" : method.Name;
                return MongoResults.Documents([await RunAsync(_connection.Admin, new BsonDocument(commandName, 1), ct).ConfigureAwait(false)]);
            case "currentOp":
                var currentOp = new BsonDocument("currentOp", 1);
                currentOp.Merge(method.Argument(0) is BsonBoolean { Value: true } ? new BsonDocument("$all", true) : method.Document(0));
                var ops = (await RunAsync(_connection.Admin, currentOp, ct).ConfigureAwait(false))["inprog"].AsBsonArray.OfType<BsonDocument>().ToList();
                return Page(ops, maxRows);
            case "killOp":
                return MongoResults.Documents([await RunAsync(_connection.Admin, new BsonDocument { { "killOp", 1 }, { "op", method.Argument(0) ?? BsonNull.Value } }, ct).ConfigureAwait(false)]);
            case "getUsers" or "getUser":
                var users = await RunAsync(database, new BsonDocument("usersInfo", method.Name == "getUser" ? method.String(0) : 1), ct).ConfigureAwait(false);
                return MongoResults.Documents(users["users"].AsBsonArray.OfType<BsonDocument>().ToList());
            case "getRoles" or "getRole":
                var roles = await RunAsync(database, new BsonDocument("rolesInfo", method.Name == "getRole" ? method.String(0) : 1), ct).ConfigureAwait(false);
                return MongoResults.Documents(roles["roles"].AsBsonArray.OfType<BsonDocument>().ToList());
            case "createUser":
                var user = method.Document(0).DeepClone().AsBsonDocument;
                var createUser = new BsonDocument("createUser", user.GetValue("user", BsonNull.Value));
                user.Remove("user");
                createUser.Merge(user, overwriteExistingElements: false);
                if (!createUser.Contains("roles"))
                {
                    createUser["roles"] = new BsonArray();
                }

                await RunAsync(database, createUser, ct).ConfigureAwait(false);
                return MongoResults.Affected(-1);
            case "dropUser":
                await RunAsync(database, new BsonDocument("dropUser", method.String(0)), ct).ConfigureAwait(false);
                return MongoResults.Affected(-1);
            case "getName":
                return MongoResults.Value("name", database.DatabaseNamespace.DatabaseName);
            case "getProfilingStatus":
                return MongoResults.Documents([await RunAsync(database, new BsonDocument("profile", -1), ct).ConfigureAwait(false)]);
            case "aggregate":
                if (command.Calls.Skip(1).Any(c => c.Name is not ("toArray" or "pretty")))
                {
                    throw new IntrospectionException("Only toArray() and pretty() may follow db.aggregate(…).");
                }

                var cursor = await database.AggregateAsync(PipelineDefinition<NoPipelineInput, BsonDocument>.Create(Stages(method)), cancellationToken: ct).ConfigureAwait(false);
                return Page(await TakeAsync(cursor, maxRows, ct).ConfigureAwait(false), maxRows);
            default:
                throw new IntrospectionException($"db.{method.Name}() is not supported in the console; db.runCommand({{…}}) runs any server command.");
        }
    }

    private async Task<QueryResult> CollectionMethodAsync(ShellCommand command, int maxRows, CancellationToken ct)
    {
        var database = Database(command);
        var name = command.Collection!;
        var collection = database.GetCollection<BsonDocument>(name);
        var method = command.Method!;

        // db.coll.explain().find(…) and db.coll.find(…).explain().
        if (command.Calls.FirstOrDefault(c => c.Name == "explain") is { } explain)
        {
            var reply = await RunAsync(database, new BsonDocument { { "explain", ExplainTarget(command) }, { "verbosity", Verbosity(explain.Argument(0)) } }, ct).ConfigureAwait(false);
            return MongoResults.Documents([reply]);
        }

        switch (method.Name)
        {
            case "find" or "findOne":
                return await FindAsync(collection, command, maxRows, ct).ConfigureAwait(false);
            case "aggregate":
                if (command.Calls.Skip(1).Any(c => c.Name is not ("toArray" or "pretty")))
                {
                    throw new IntrospectionException("Only toArray(), pretty(), and explain() may follow aggregate(…).");
                }

                var settings = method.Argument(0) is BsonArray ? method.Document(1) : [];
                var options = new AggregateOptions
                {
                    AllowDiskUse = settings.TryGetValue("allowDiskUse", out var disk) ? disk.ToBoolean() : null,
                    Collation = Collation(settings),
                    Hint = settings.GetValue("hint", null),
                    MaxTime = settings.TryGetValue("maxTimeMS", out var maxTime) ? TimeSpan.FromMilliseconds(maxTime.ToDouble()) : null,
                    Let = settings.GetValue("let", null) as BsonDocument,
                };
                var cursor = await collection.AggregateAsync(PipelineDefinition<BsonDocument, BsonDocument>.Create(Stages(method)), options, ct).ConfigureAwait(false);
                return Page(await TakeAsync(cursor, maxRows, ct).ConfigureAwait(false), maxRows);
            case "countDocuments" or "count":
                var countSettings = method.Document(1);
                var count = await collection.CountDocumentsAsync(method.Document(0), new CountOptions
                {
                    Limit = countSettings.TryGetValue("limit", out var limit) ? limit.ToInt64() : null,
                    Skip = countSettings.TryGetValue("skip", out var skip) ? skip.ToInt64() : null,
                    Hint = countSettings.GetValue("hint", null),
                    Collation = Collation(countSettings),
                }, ct).ConfigureAwait(false);
                return MongoResults.Value("count", count);
            case "estimatedDocumentCount":
                return MongoResults.Value("count", await collection.EstimatedDocumentCountAsync(cancellationToken: ct).ConfigureAwait(false));
            case "distinct":
                var field = method.String(0);
                var values = await (await collection.DistinctAsync<BsonValue>(field, method.Document(1), cancellationToken: ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false);
                return MongoResults.Values(field, values.Take(maxRows).ToList()) with { Truncated = values.Count > maxRows };
            case "insertOne":
                await collection.InsertOneAsync(method.Document(0), cancellationToken: ct).ConfigureAwait(false);
                return MongoResults.Affected(1);
            case "insertMany" or "insert":
                var documents = method.Argument(0) switch
                {
                    BsonArray array => array.Select(d => d as BsonDocument ?? throw new IntrospectionException($"{method.Name}(): every document must be a document.")).ToList(),
                    BsonDocument one => [one],
                    _ => throw new IntrospectionException($"{method.Name}(): pass an array of documents."),
                };
                if (documents.Count > 0)
                {
                    await collection.InsertManyAsync(documents, new InsertManyOptions { IsOrdered = method.Document(1).GetValue("ordered", true).ToBoolean() }, ct).ConfigureAwait(false);
                }

                return MongoResults.Affected(documents.Count);
            case "updateOne" or "updateMany" or "update":
                var updateSettings = method.Document(2);
                var updateOptions = new UpdateOptions
                {
                    IsUpsert = updateSettings.GetValue("upsert", false).ToBoolean(),
                    Collation = Collation(updateSettings),
                    Hint = updateSettings.GetValue("hint", null),
                    ArrayFilters = updateSettings.GetValue("arrayFilters", null) is BsonArray filters
                        ? filters.OfType<BsonDocument>().Select(f => (ArrayFilterDefinition)new BsonDocumentArrayFilterDefinition<BsonDocument>(f)).ToList()
                        : null,
                };
                var updateMany = method.Name == "updateMany" || method.Name == "update" && updateSettings.GetValue("multi", false).ToBoolean();
                var updated = updateMany
                    ? await collection.UpdateManyAsync(method.Document(0), Update(method, 1), updateOptions, ct).ConfigureAwait(false)
                    : await collection.UpdateOneAsync(method.Document(0), Update(method, 1), updateOptions, ct).ConfigureAwait(false);
                return MongoResults.Affected(updated.ModifiedCount + (updated.UpsertedId is null ? 0 : 1));
            case "replaceOne":
                var replaced = await collection.ReplaceOneAsync(method.Document(0), method.Document(1),
                    new ReplaceOptions { IsUpsert = method.Document(2).GetValue("upsert", false).ToBoolean() }, ct).ConfigureAwait(false);
                return MongoResults.Affected(replaced.ModifiedCount + (replaced.UpsertedId is null ? 0 : 1));
            case "deleteOne":
                return MongoResults.Affected((await collection.DeleteOneAsync(method.Document(0), ct).ConfigureAwait(false)).DeletedCount);
            case "deleteMany" or "remove":
                var justOne = method.Name == "remove" && method.Argument(1) is BsonBoolean { Value: true };
                var deleted = justOne
                    ? await collection.DeleteOneAsync(method.Document(0), ct).ConfigureAwait(false)
                    : await collection.DeleteManyAsync(method.Document(0), ct).ConfigureAwait(false);
                return MongoResults.Affected(deleted.DeletedCount);
            case "findOneAndUpdate" or "findOneAndReplace" or "findOneAndDelete":
                return await FindOneAndModifyAsync(collection, method, ct).ConfigureAwait(false);
            case "createIndex" or "ensureIndex" or "createIndexes":
                var keys = method.Name == "createIndexes"
                    ? method.Array(0).Select(k => k as BsonDocument ?? throw new IntrospectionException("createIndexes(): every key pattern must be a document.")).ToList()
                    : [method.Document(0)];
                var indexOptions = method.Document(1);
                var indexes = new BsonArray();
                foreach (var key in keys)
                {
                    var index = new BsonDocument { { "key", key }, { "name", keys.Count == 1 && indexOptions.Contains("name") ? indexOptions["name"] : IndexName(key) } };
                    index.Merge(indexOptions, overwriteExistingElements: false);
                    indexes.Add(index);
                }

                await RunAsync(database, new BsonDocument { { "createIndexes", name }, { "indexes", indexes } }, ct).ConfigureAwait(false);
                return MongoResults.Values("name", indexes.Select(i => i["name"]).ToList());
            case "dropIndex" or "dropIndexes":
                var which = method.Argument(0) ?? (method.Name == "dropIndexes" ? "*" : throw new IntrospectionException("dropIndex(): name the index, or pass its key pattern."));
                await RunAsync(database, new BsonDocument { { "dropIndexes", name }, { "index", which } }, ct).ConfigureAwait(false);
                return MongoResults.Affected(-1);
            case "hideIndex" or "unhideIndex":
                var target = method.Argument(0) is BsonString { Value: var indexName }
                    ? new BsonDocument("name", indexName)
                    : new BsonDocument("keyPattern", method.Document(0));
                target["hidden"] = method.Name == "hideIndex";
                await RunAsync(database, new BsonDocument { { "collMod", name }, { "index", target } }, ct).ConfigureAwait(false);
                return MongoResults.Affected(-1);
            case "getIndexes" or "indexes" or "getIndexKeys":
                var list = await TakeAsync(await collection.Indexes.ListAsync(ct).ConfigureAwait(false), maxRows, ct).ConfigureAwait(false);
                return Page(method.Name == "getIndexKeys" ? list.Select(i => i["key"].AsBsonDocument).ToList() : list, maxRows);
            case "drop":
                await RunAsync(database, new BsonDocument("drop", name), ct).ConfigureAwait(false);
                return MongoResults.Affected(-1);
            case "renameCollection":
                var databaseName = database.DatabaseNamespace.DatabaseName;
                await RunAsync(_connection.Admin, new BsonDocument
                {
                    { "renameCollection", $"{databaseName}.{name}" },
                    { "to", $"{databaseName}.{method.String(0)}" },
                    { "dropTarget", method.Argument(1) is BsonBoolean { Value: true } },
                }, ct).ConfigureAwait(false);
                return MongoResults.Affected(-1);
            case "stats" or "latencyStats" or "dataSize" or "storageSize" or "totalIndexSize" or "isCapped":
                var stage = method.Name == "latencyStats" ? new BsonDocument("latencyStats", new BsonDocument()) : new BsonDocument("storageStats", new BsonDocument());
                var statsCursor = await collection.AggregateAsync(
                    PipelineDefinition<BsonDocument, BsonDocument>.Create([new BsonDocument("$collStats", stage)]), cancellationToken: ct).ConfigureAwait(false);
                var stats = (await TakeAsync(statsCursor, 1, ct).ConfigureAwait(false)).FirstOrDefault() ?? [];
                var storage = stats.GetValue("storageStats", new BsonDocument()).AsBsonDocument;
                return method.Name switch
                {
                    "dataSize" => MongoResults.Value("size", storage.GetValue("size", 0)),
                    "storageSize" => MongoResults.Value("storageSize", storage.GetValue("storageSize", 0)),
                    "totalIndexSize" => MongoResults.Value("totalIndexSize", storage.GetValue("totalIndexSize", 0)),
                    "isCapped" => MongoResults.Value("capped", storage.GetValue("capped", false)),
                    _ => MongoResults.Documents([stats]),
                };
            case "validate":
                return MongoResults.Documents([await RunAsync(database, new BsonDocument { { "validate", name }, { "full", method.Document(0).GetValue("full", false) } }, ct).ConfigureAwait(false)]);
            default:
                throw new IntrospectionException($"{method.Name}() is not supported in the console; db.runCommand({{…}}) runs any server command.");
        }
    }

    private static async Task<QueryResult> FindAsync(IMongoCollection<BsonDocument> collection, ShellCommand command, int maxRows, CancellationToken ct)
    {
        var method = command.Method!;
        var filter = method.Document(0);
        var options = new FindOptions<BsonDocument> { Projection = method.OptionalDocument(1) };
        long? limit = method.Name == "findOne" ? 1 : null;
        var count = false;
        foreach (var call in command.Calls.Skip(1))
        {
            switch (call.Name)
            {
                case "sort":
                    options.Sort = call.Document(0);
                    break;
                case "limit":
                    limit = Math.Abs(call.Number(0)) is var n and > 0 ? n : null;
                    break;
                case "skip":
                    options.Skip = (int)call.Number(0);
                    break;
                case "projection" or "project":
                    options.Projection = call.Document(0);
                    break;
                case "hint":
                    options.Hint = call.Argument(0);
                    break;
                case "collation":
                    options.Collation = MongoDB.Driver.Collation.FromBsonDocument(call.Document(0));
                    break;
                case "maxTimeMS":
                    options.MaxTime = TimeSpan.FromMilliseconds(call.Number(0));
                    break;
                case "comment":
                    options.Comment = call.Argument(0);
                    break;
                case "allowDiskUse":
                    options.AllowDiskUse = call.Argument(0)?.ToBoolean() ?? true;
                    break;
                case "batchSize":
                    options.BatchSize = (int)call.Number(0);
                    break;
                case "count" or "size" or "itcount":
                    count = true;
                    break;
                case "toArray" or "pretty" or "noCursorTimeout" or "readPref" or "returnKey" or "showRecordId":
                    break;
                default:
                    throw new IntrospectionException($"{call.Name}() is not supported on a find cursor.");
            }
        }

        if (count)
        {
            // count() counts the filter's matches; size() and itcount() honor skip and limit.
            var applyLimits = command.Calls.Any(c => c.Name is "size" or "itcount");
            var total = await collection.CountDocumentsAsync(filter, new CountOptions
            {
                Skip = applyLimits ? options.Skip : null,
                Limit = applyLimits ? limit : null,
                Hint = options.Hint,
                Collation = options.Collation,
            }, ct).ConfigureAwait(false);
            return MongoResults.Value("count", total);
        }

        options.Limit = (int)Math.Min(limit ?? long.MaxValue, maxRows + 1L);
        var documents = await TakeAsync(await collection.FindAsync(filter, options, ct).ConfigureAwait(false), maxRows, ct).ConfigureAwait(false);
        return Page(documents, maxRows);
    }

    private static async Task<QueryResult> FindOneAndModifyAsync(IMongoCollection<BsonDocument> collection, ShellCall method, CancellationToken ct)
    {
        var filter = method.Document(0);
        var settings = method.Name == "findOneAndDelete" ? method.Document(1) : method.Document(2);
        var after = settings.GetValue("returnNewDocument", false).ToBoolean()
            || settings.GetValue("returnDocument", "before") is BsonString { Value: "after" };
        var projection = settings.GetValue("projection", null) as BsonDocument;
        var sort = settings.GetValue("sort", null) as BsonDocument;
        var upsert = settings.GetValue("upsert", false).ToBoolean();
        var document = method.Name switch
        {
            "findOneAndUpdate" => await collection.FindOneAndUpdateAsync(filter, Update(method, 1), new FindOneAndUpdateOptions<BsonDocument>
            {
                Projection = projection,
                Sort = sort,
                IsUpsert = upsert,
                ReturnDocument = after ? ReturnDocument.After : ReturnDocument.Before,
            }, ct).ConfigureAwait(false),
            "findOneAndReplace" => await collection.FindOneAndReplaceAsync(filter, method.Document(1), new FindOneAndReplaceOptions<BsonDocument>
            {
                Projection = projection,
                Sort = sort,
                IsUpsert = upsert,
                ReturnDocument = after ? ReturnDocument.After : ReturnDocument.Before,
            }, ct).ConfigureAwait(false),
            _ => await collection.FindOneAndDeleteAsync(filter, new FindOneAndDeleteOptions<BsonDocument> { Projection = projection, Sort = sort }, ct).ConfigureAwait(false),
        };
        return MongoResults.Documents(document is null ? [] : [document]);
    }
}
