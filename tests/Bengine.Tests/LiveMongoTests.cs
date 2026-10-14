namespace Bengine.Tests;

using System.Globalization;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Activity;
using Bengine.Introspection.Model;
using Bengine.Introspection.Mongo;
using Bengine.Introspection.Plans;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Sql;
using Bengine.Introspection.Tree;
using MongoDB.Bson;
using Xunit.Abstractions;

/// <summary>
/// MongoDB, live: the tree, the shell console, the data editor, the catalog
/// and completion, explain, server activity, and the explorer's commands
/// against a real server. Each test works in a scratch database it drops.
/// Runs when <c>BENGINE_TEST_MONGO</c> holds
/// <c>host;port;authDatabase;user;password</c>.
/// </summary>
[Collection("live-postgres")]
public sealed class LiveMongoTests(ITestOutputHelper output)
{
    private static readonly string? Spec = Environment.GetEnvironmentVariable("BENGINE_TEST_MONGO");

    private const string Setup = """
        db.customers.insertMany([
          { _id: 1, email: "ann@example.com", name: "Ann", born: ISODate("1984-05-06T00:00:00Z"), balance: NumberDecimal("12.50"),
            address: { city: "Tashkent", geo: { lat: 41.3, lng: 69.2 } }, tags: ["vip", "new"] },
          { _id: 2, email: "bob@example.com", name: "Bob", balance: NumberDecimal("0"), address: { city: "Aktau" } },
          { _id: 3, email: "cy@example.com", name: "Cy", orders: [{ sku: "a", qty: 2 }, { sku: "b", qty: 1 }] }
        ])
        db.customers.createIndex({ email: 1 }, { unique: true })
        db.customers.createIndex({ "address.city": 1, name: -1 })
        db.orders.insertMany([{ customer: 1, total: 120.5 }, { customer: 1, total: 15 }, { customer: 2, total: 300 }])
        db.createView("big_orders", "orders", [{ $match: { total: { $gt: 100 } } }])
        db.createCollection("events", { capped: true, size: 65536, validator: { kind: { $type: "string" } } })
        """;

    [Fact]
    public async Task Walks_the_tree_and_reads_every_object_when_configured()
    {
        if (Spec is null)
        {
            output.WriteLine("BENGINE_TEST_MONGO is not set; skipping.");
            return;
        }

        await WithScratchAsync(async (server, database) =>
        {
            await RunAsync(server, database, Setup);

            var failures = new List<string>();
            var visited = new List<TreeNode>();
            var queue = new Queue<TreeNode>([new TreeNode { Ref = new NodeRef { Kind = NodeKind.Server }, Label = server.Name, HasChildren = true }]);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                visited.Add(node);
                try
                {
                    var properties = await ObjectExplorer.GetPropertiesAsync(server, null, node.Ref);
                    Assert.False(string.IsNullOrEmpty(properties.Title));
                    if (node.Ref.Kind is NodeKind.Table or NodeKind.View or NodeKind.Index && node.Ref.Database == database)
                    {
                        Assert.False(string.IsNullOrEmpty(properties.Sql), $"No script for {node.Ref.Id}");
                        Assert.NotEmpty(SqlScript.Split(properties.Sql!, DatabaseProvider.MongoDb).Select(s => MongoShell.Parse(s.Text)));
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"properties of {node.Ref.Id}: {ex.Message}");
                }

                if (!node.HasChildren)
                {
                    continue;
                }

                try
                {
                    var children = await ObjectExplorer.GetChildrenAsync(server, null, node.Ref);
                    var mine = node.Ref.Kind == NodeKind.DatabasesFolder ? children.Where(c => c.Ref.Name == database || c.Ref.Name == "admin") : children;
                    foreach (var child in mine)
                    {
                        queue.Enqueue(child);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"children of {node.Ref.Id}: {ex.Message}");
                }
            }

            output.WriteLine(string.Join('\n', visited.Select(v => $"{v.Ref.Kind}: {v.Ref.Database}/{v.Label}{(v.Count is { } c ? $" ({c})" : string.Empty)}{(v.Detail is { } d ? $"  [{d}]" : string.Empty)}{(v.Badge is { } b ? $" {b}" : string.Empty)}")));
            output.WriteLine(string.Join('\n', failures));
            Assert.Empty(failures);
            foreach (var kind in new[] { NodeKind.Database, NodeKind.Table, NodeKind.View, NodeKind.Column, NodeKind.Index, NodeKind.LoginRole })
            {
                Assert.Contains(visited, v => v.Ref.Kind == kind);
            }

            Assert.Contains(visited, v => v.Ref is { Kind: NodeKind.Column, Name: "address.geo.lat" });
            Assert.Contains(visited, v => v.Ref is { Kind: NodeKind.Column, Name: "orders.sku" });
            Assert.Contains(visited, v => v.Ref is { Kind: NodeKind.Table, Name: "events" } && v.Detail == "capped");
            Assert.Contains(visited, v => v.Ref is { Kind: NodeKind.Index, Name: "email_1" } && v.Badge == "UQ");
        });
    }

    [Fact]
    public async Task Runs_the_console_and_explains_when_configured()
    {
        if (Spec is null)
        {
            output.WriteLine("BENGINE_TEST_MONGO is not set; skipping.");
            return;
        }

        await WithScratchAsync(async (server, database) =>
        {
            await RunAsync(server, database, Setup);
            await using var session = await ObjectExplorer.OpenSessionAsync(server, null, database);
            Assert.Equal(database, session.Database);
            async Task<QueryResult> Run(string command, int maxRows = 1000) => await session.Query.ExecuteAsync(command, maxRows);

            Assert.Contains(database, (await Run("show dbs")).Rows.Select(r => r[0] as string));
            Assert.Equal(["customers", "events", "orders", "big_orders"], (await Run("show collections")).Rows.Select(r => (string)r[0]!).OrderBy(n => n == "big_orders").ThenBy(n => n));

            var found = await Run("db.customers.find({ balance: { $exists: true } }, { name: 1, address: 1 }).sort({ name: -1 }).limit(5)");
            Assert.Equal(["_id", "name", "address"], found.Columns);
            Assert.Equal(["Bob", "Ann"], found.Rows.Select(r => r[1]));
            Assert.Equal("object", found.ColumnTypes[2]);
            Assert.Equal("{city: \"Aktau\"}", found.Rows[0][2]!.ToString());

            var truncated = await Run("db.customers.find()", maxRows: 2);
            Assert.True(truncated.Truncated);
            Assert.Equal(2, truncated.Rows.Count);

            Assert.Equal(2L, (await Run("db.customers.countDocuments({ balance: { $exists: true } })")).Rows[0][0]);
            Assert.Equal(3L, (await Run("db.customers.find().count()")).Rows[0][0]);
            Assert.Equal(["Aktau", "Tashkent"], (await Run("db.customers.distinct('address.city', { address: { $exists: true } })")).Rows.Select(r => r[0]).Order());
            var grouped = await Run("db.orders.aggregate([{ $group: { _id: '$customer', total: { $sum: '$total' } } }, { $sort: { _id: 1 } }])");
            Assert.Equal(135.5, grouped.Rows[0][1]);
            Assert.Equal(2, (await Run("db.big_orders.find()")).Rows.Count);

            Assert.Equal(1, (await Run("db.customers.updateOne({ _id: 2 }, { $set: { name: 'Bobby' }, $inc: { visits: 1 } })")).RowsAffected);
            Assert.Equal(3, (await Run("db.customers.updateMany({}, [{ $set: { seen: true } }])")).RowsAffected);
            var after = await Run("db.customers.findOneAndUpdate({ _id: 3 }, { $set: { name: 'Cyrus' } }, { returnDocument: 'after', projection: { name: 1 } })");
            Assert.Equal("Cyrus", after.Rows[0][1]);
            Assert.Equal(2, (await Run("db.customers.insertMany([{ _id: 10, email: 'j@example.com' }, { _id: 11, name: 'K', email: 'k@example.com' }])")).RowsAffected);
            Assert.Equal(1, (await Run("db.customers.deleteOne({ _id: 10 })")).RowsAffected);
            Assert.Equal(["kind_1"], (await Run("db.events.createIndex({ kind: 1 })")).Rows.Select(r => r[0]));
            Assert.Contains("kind_1", (await Run("db.events.getIndexes()")).Rows.Select(r => r[2] as string ?? r.OfType<string>().FirstOrDefault()));
            Assert.Equal("1", (await Run("db.runCommand({ ping: 1 })")).Rows[0][0]?.ToString());
            Assert.Single((await Run("{ listCollections: 1, filter: { name: 'orders' } }")).Rows);
            Assert.NotNull((await Run("db.version()")).Rows[0][0]);
            Assert.Contains("queryPlanner", (await Run("db.customers.find({ email: 'ann@example.com' }).explain()")).Columns);

            var error = await Assert.ThrowsAsync<IntrospectionException>(() => Run("db.events.insertOne({ kind: 5 })"));
            output.WriteLine(error.Message);
            Assert.Contains("validation", error.Message, StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAsync<IntrospectionException>(() => Run("db.customers.frobnicate()"));
            await Assert.ThrowsAsync<IntrospectionException>(() => Run("db.customers.insertOne({ _id: 1 })"));

            // Explain: an index seek for the unique email, a collection scan for an unindexed field.
            var seek = await PlanReader.ExplainAsync(session, "db.customers.find({ email: 'ann@example.com' })", analyze: false);
            output.WriteLine(string.Join('\n', seek.Flatten().Select(n => new string(' ', n.Depth * 2) + n.Node.Operation + " " + n.Node.Object)));
            Assert.Contains(seek.Flatten(), n => n.Node.Operation is "IXSCAN" or "EXPRESS_IXSCAN" && n.Node.Object == "email_1");
            var scan = await PlanReader.ExplainAsync(session, "db.customers.find({ name: 'Ann' }).sort({ balance: 1 })", analyze: true);
            output.WriteLine(string.Join('\n', scan.Flatten().Select(n => new string(' ', n.Depth * 2) + n.Node.Operation + " rows=" + n.Node.ActualRows + " " + string.Join("; ", n.Node.Warnings))));
            Assert.True(scan.Analyzed);
            Assert.Contains(scan.Flatten(), n => n.Node.Warnings.Count > 0);
            var pipeline = await PlanReader.ExplainAsync(session, "db.orders.aggregate([{ $group: { _id: '$customer', n: { $sum: 1 } } }])", analyze: false);
            Assert.NotEmpty(pipeline.Roots);

            // The activity view lists this connection's own operation; killOp of a finished op is harmless.
            var sessions = await ServerActivity.SessionsAsync(session);
            Assert.Contains(sessions, s => s.IsSelf);
            Assert.Empty(await ServerActivity.LocksAsync(session));
            await ServerActivity.CancelAsync(session, "2147483000");

            // A read-only registration refuses writes before sending them.
            await using var readOnly = await ObjectExplorer.OpenSessionAsync(server with { ReadOnly = true }, null, database);
            Assert.Equal(3, (await readOnly.Query.ExecuteAsync("db.orders.find()")).Rows.Count);
            await Assert.ThrowsAsync<IntrospectionException>(() => readOnly.Query.ExecuteAsync("db.orders.deleteMany({})"));
            Assert.Equal(3L, (await Run("db.orders.countDocuments()")).Rows[0][0]);
        });
    }

    [Fact]
    public async Task Edits_data_and_reads_the_catalog_when_configured()
    {
        if (Spec is null)
        {
            output.WriteLine("BENGINE_TEST_MONGO is not set; skipping.");
            return;
        }

        await WithScratchAsync(async (server, database) =>
        {
            await RunAsync(server, database, Setup);
            await using var session = await ObjectExplorer.OpenSessionAsync(server, null, database);
            var customers = new TableRef("customers");

            var page = await session.Data.QueryAsync(customers, new DataQuery { PageSize = 2, IncludeTotalCount = true, OrderBySql = "name: 1" });
            Assert.Equal(3, page.TotalCount);
            Assert.True(page.HasMore);
            Assert.Equal(["Ann", "Bob"], page.Rows.Select(r => r[page.Columns.ToList().IndexOf("name")]));
            Assert.Equal("db.customers.find({}).sort({name: 1})", page.Sql);
            var filtered = await session.Data.QueryAsync(customers, new DataQuery
            {
                WhereSql = "{ 'address.city': 'Tashkent' }",
                Filters = [FilterCondition.Contains("email", "ANN")],
            });
            Assert.Single(filtered.Rows);
            Assert.Equal("decimal", filtered.ColumnTypes[filtered.Columns.ToList().IndexOf("balance")]);
            await Assert.ThrowsAsync<IntrospectionException>(() => session.Data.QueryAsync(customers, new DataQuery { WhereSql = "[1]" }));

            // Insert (server-generated _id), update with $set/$unset, delete — then read back.
            var annId = page.Rows[0][0];
            IReadOnlyList<RowChange> changes =
            [
                RowChange.Insert(new Dictionary<string, object?> { ["_id"] = null, ["name"] = "Dee", ["email"] = "dee@example.com", ["tags"] = SqlDefault.Value }),
                RowChange.Update(new Dictionary<string, object?> { ["_id"] = annId }, new Dictionary<string, object?>
                {
                    ["name"] = "Anna",
                    ["address"] = MongoValues.FromText("{city: 'Samarkand'}", "object"),
                    ["tags"] = SqlDefault.Value,
                }),
                RowChange.Delete(new Dictionary<string, object?> { ["_id"] = 2 }),
            ];
            var preview = MongoDataWriter.Preview(customers, changes);
            output.WriteLine(string.Join('\n', preview));
            Assert.Equal("db.customers.updateOne({_id: 1}, {$set: {name: \"Anna\", address: {city: \"Samarkand\"}}, $unset: {tags: \"\"}});", preview[1]);
            Assert.All(preview, p => MongoShell.Parse(p));
            Assert.Equal([1, 1, 1], await session.Writer.ApplyAsync(customers, changes));

            var all = await session.Query.ExecuteAsync("db.customers.find().sort({ _id: 1 })");
            var rows = all.Rows.Select(r => all.Columns.Zip(r).ToDictionary(p => p.First, p => p.Second)).ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal("Anna", rows[0]["name"]);
            Assert.Null(rows[0].GetValueOrDefault("tags"));
            Assert.Equal("{city: \"Samarkand\"}", rows[0]["address"]!.ToString());
            Assert.Contains(rows, r => r["name"] as string == "Dee" && r["_id"] is BsonCell { Value.BsonType: BsonType.ObjectId });

            // A change that finds no document fails (on a standalone server earlier changes stay).
            var missing = await Assert.ThrowsAsync<IntrospectionException>(() => session.Writer.ApplyAsync(customers,
                [RowChange.Update(new Dictionary<string, object?> { ["_id"] = 999 }, new Dictionary<string, object?> { ["name"] = "x" })]));
            Assert.Contains("found 0 documents", missing.Message, StringComparison.Ordinal);
            Assert.Equal(1, await session.Writer.InsertManyAsync(new TableRef("orders"), [new Dictionary<string, object?> { ["customer"] = 3, ["total"] = 9.5m }]));

            // Catalog and completion.
            var catalog = await session.ReadCatalogAsync();
            Assert.Equal(DatabaseProvider.MongoDb, catalog.Provider);
            var relation = catalog.Find(null, "customers")!;
            Assert.True(relation.Column("_id")!.IsPrimaryKey);
            Assert.NotNull(relation.Column("address.city"));
            Assert.Equal(TableKind.View, catalog.Find(null, "big_orders")!.Kind);
            Assert.Contains("customers", SqlCompletion.Complete("db.", 3, catalog).Items.Select(i => i.Label));
            Assert.Contains("\"address.city\"", SqlCompletion.Complete("db.customers.find({ addr", 24, catalog).Items.Select(i => i.InsertText));

            // Schema inference: _id is the key, indexes come along.
            var table = await session.Introspector.IntrospectTableAsync(customers);
            Assert.Equal(["_id"], table.PrimaryKey!.ColumnNames);
            Assert.Contains(table.Indexes, i => i.Name == "email_1" && i.Type == IndexType.Unique);
            Assert.Equal(DbDataType.Json, table.Columns.First(c => c.Name == "address").Type.DataType);
            Assert.Contains(database, await session.Explorer.GetDatabasesAsync());
            Assert.Contains(await session.Explorer.GetTablesAsync(), t => t.Name == "big_orders" && t.Kind == TableKind.View);

            // The explorer's commands run in the console.
            var orders = new NodeRef { Kind = NodeKind.Table, Database = database, Relation = "orders", Name = "orders" };
            await session.Query.ExecuteAllAsync(ObjectSql.Rename(DatabaseProvider.MongoDb, orders with { Kind = NodeKind.Column, Name = "total" }, "amount"));
            Assert.Contains("amount", (await session.Query.ExecuteAsync("db.orders.findOne()")).Columns);
            await session.Query.ExecuteAllAsync(ObjectSql.Rename(DatabaseProvider.MongoDb, orders, "purchases"));
            await session.Query.ExecuteAllAsync(ObjectSql.Truncate(DatabaseProvider.MongoDb, orders with { Name = "purchases" }));
            Assert.Equal(0L, (await session.Query.ExecuteAsync("db.purchases.countDocuments()")).Rows[0][0]);
            await session.Query.ExecuteAllAsync(ObjectSql.Drop(DatabaseProvider.MongoDb, orders with { Name = "purchases" }));
            Assert.DoesNotContain(await session.Explorer.GetTablesAsync(), t => t.Name == "purchases");
        });
    }

    [Fact]
    public async Task Asks_for_the_password_when_the_login_is_refused_when_configured()
    {
        if (Spec is null)
        {
            output.WriteLine("BENGINE_TEST_MONGO is not set; skipping.");
            return;
        }

        var server = Admin() with { Id = Guid.NewGuid().ToString("N"), Password = "wrong-password" };
        try
        {
            var ex = await Assert.ThrowsAsync<IntrospectionException>(() => ObjectExplorer.TestAsync(server, null));
            output.WriteLine(ex.Message);
            Assert.True(ObjectExplorer.IsAuthenticationError(ex));

            // No credentials at all: listing databases needs a login.
            var anonymous = await Assert.ThrowsAsync<IntrospectionException>(() =>
                ObjectExplorer.GetChildrenAsync(server with { Username = null, Password = null }, null, new NodeRef { Kind = NodeKind.DatabasesFolder }));
            output.WriteLine(anonymous.Message);
            Assert.True(ObjectExplorer.IsAuthenticationError(anonymous));

            var unreachable = await Assert.ThrowsAsync<IntrospectionException>(() => ObjectExplorer.TestAsync(server with { Port = 1, Options = "serverSelectionTimeoutMS=1500" }, null));
            output.WriteLine(unreachable.Message);
            Assert.False(ObjectExplorer.IsAuthenticationError(unreachable));
        }
        finally
        {
            ObjectExplorer.Disconnect(server.Id);
        }
    }

    private static ServerProfile Admin()
    {
        var p = Spec!.Split(';');
        return new ServerProfile
        {
            Name = "mongo", Provider = DatabaseProvider.MongoDb, Host = p[0], Port = int.Parse(p[1], CultureInfo.InvariantCulture),
            MaintenanceDatabase = p[2], Username = p[3], Password = p[4], SavePassword = true,
        };
    }

    private static async Task RunAsync(ServerProfile server, string database, string script)
    {
        await using var session = await ObjectExplorer.OpenSessionAsync(server, null, database);
        foreach (var statement in SqlScript.Split(script, DatabaseProvider.MongoDb))
        {
            await session.Query.ExecuteAllAsync(statement.Text);
        }
    }

    private async Task WithScratchAsync(Func<ServerProfile, string, Task> test)
    {
        var server = Admin() with { Id = Guid.NewGuid().ToString("N") };
        var database = $"bengine_{Guid.NewGuid():N}"[..20];
        try
        {
            await test(server, database);
        }
        finally
        {
            await using (var session = await ObjectExplorer.OpenSessionAsync(server, null, database))
            {
                await session.Query.ExecuteAllAsync("db.dropDatabase()");
            }

            ObjectExplorer.Disconnect(server.Id);
            output.WriteLine($"dropped {database}");
        }
    }
}
