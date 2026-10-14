namespace Bengine.Tests;

using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Model;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Sql;
using Bengine.Introspection.Tree;
using Xunit.Abstractions;

/// <summary>
/// Live tests of the MySQL, MariaDB, and SQL Server support, which otherwise is
/// only compiled: each walks the whole object tree (every node's children and
/// properties), reads the catalog, pages and filters data, applies staged
/// changes, runs batches, and explains a query — in a scratch database created
/// for the run. They run when <c>BENGINE_TEST_MYSQL</c>,
/// <c>BENGINE_TEST_MARIADB</c>, or <c>BENGINE_TEST_MSSQL</c> hold
/// <c>host;port;database;user;password</c>.
/// </summary>
[Collection("live-postgres")]
public sealed class LiveProviderTests(ITestOutputHelper output)
{
    private const string MySqlSetup = """
        CREATE TABLE users (id int AUTO_INCREMENT PRIMARY KEY, email varchar(120) NOT NULL UNIQUE, score bigint, born date, active tinyint(1) DEFAULT 1, CONSTRAINT ck_score CHECK (score >= 0));
        CREATE TABLE orders (id int AUTO_INCREMENT PRIMARY KEY, user_id int NOT NULL, total decimal(10,2), FOREIGN KEY (user_id) REFERENCES users(id));
        CREATE INDEX ix_orders_total ON orders(total);
        CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;
        CREATE TRIGGER trg_orders BEFORE INSERT ON orders FOR EACH ROW SET NEW.total = COALESCE(NEW.total, 0);
        CREATE PROCEDURE touch_user(IN uid int) BEGIN UPDATE users SET score = score + 1 WHERE id = uid; END;
        CREATE FUNCTION twice(x int) RETURNS int DETERMINISTIC RETURN x * 2;
        INSERT INTO users (email, score, born) VALUES ('a@x.io', 9007199254740993, '1990-01-02'), ('b@x.io', 5, NULL), ('c@x.io', 0, '2001-05-06');
        INSERT INTO orders (user_id, total) VALUES (1, 150), (1, 20), (3, NULL);
        """;

    private const string SqlServerSetup = """
        CREATE TABLE users (id int IDENTITY(1,1) PRIMARY KEY, email nvarchar(120) NOT NULL UNIQUE, score bigint, born date, active bit DEFAULT 1, CONSTRAINT ck_score CHECK (score >= 0));
        CREATE TABLE orders (id int IDENTITY(1,1) PRIMARY KEY, user_id int NOT NULL REFERENCES users(id), total decimal(10,2), uid uniqueidentifier DEFAULT NEWID());
        CREATE INDEX ix_orders_total ON orders(total);
        GO
        CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;
        GO
        CREATE TRIGGER trg_orders ON orders AFTER INSERT AS BEGIN SET NOCOUNT ON; END;
        GO
        CREATE PROCEDURE touch_user @uid int AS UPDATE users SET score = score + 1 WHERE id = @uid;
        GO
        CREATE FUNCTION dbo.twice(@x int) RETURNS int AS BEGIN RETURN @x * 2; END;
        GO
        CREATE SEQUENCE seq_tickets START WITH 1 INCREMENT BY 1;
        INSERT INTO users (email, score, born) VALUES (N'a@x.io', 9007199254740993, '1990-01-02'), (N'b@x.io', 5, NULL), (N'c@x.io', 0, '2001-05-06');
        INSERT INTO orders (user_id, total) VALUES (1, 150), (1, 20), (3, NULL);
        """;

    [Fact]
    public async Task Exercises_mysql_and_mariadb_when_configured()
    {
        var ran = false;
        foreach (var variable in new[] { "BENGINE_TEST_MYSQL", "BENGINE_TEST_MARIADB" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } spec)
            {
                ran = true;
                output.WriteLine($"=== {variable}");
                await ExerciseAsync(DatabaseProvider.MySql, spec, MySqlSetup);
            }
        }

        if (!ran)
        {
            output.WriteLine("BENGINE_TEST_MYSQL / BENGINE_TEST_MARIADB are not set; skipping.");
        }
    }

    [Fact]
    public async Task Exercises_sql_server_when_configured()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_MSSQL") is not { Length: > 0 } spec)
        {
            output.WriteLine("BENGINE_TEST_MSSQL is not set; skipping.");
            return;
        }

        await ExerciseAsync(DatabaseProvider.SqlServer, spec, SqlServerSetup);
    }

    private async Task ExerciseAsync(DatabaseProvider provider, string spec, string setup)
    {
        var p = spec.Split(';');
        var scratch = $"bengine_live_{Guid.NewGuid():N}"[..24];
        var admin = new ServerProfile
        {
            Name = "admin",
            Provider = provider,
            Host = p[0],
            Port = int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
            MaintenanceDatabase = p[2],
            Username = p[3],
            Password = p[4],
            SavePassword = true,
            Options = provider == DatabaseProvider.SqlServer ? "TrustServerCertificate=True" : null,
        };

        await RunAsync(admin, null, $"CREATE DATABASE {scratch}");
        var server = admin with { Name = "live", MaintenanceDatabase = scratch };
        try
        {
            foreach (var statement in SqlScript.Split(setup, provider))
            {
                await RunAsync(server, scratch, statement.Text);
            }

            await WalkAsync(server);
            await DataAsync(server, scratch);
        }
        finally
        {
            MySqlConnector.MySqlConnection.ClearAllPools();
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            if (provider == DatabaseProvider.SqlServer)
            {
                await RunAsync(admin, null, $"ALTER DATABASE {scratch} SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            }

            await RunAsync(admin, null, $"DROP DATABASE {scratch}");
        }
    }

    private async Task WalkAsync(ServerProfile server)
    {
        var queue = new Queue<(TreeNode Node, int Depth)>();
        queue.Enqueue((new TreeNode { Ref = new NodeRef { Kind = NodeKind.Server }, Label = server.Name, HasChildren = true }, 0));
        var failures = new List<string>();
        var kinds = new HashSet<NodeKind>();
        var count = 0;
        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            count++;
            kinds.Add(node.Ref.Kind);

            // Only the scratch database is walked; others belong to someone else.
            if (node.Ref.Kind == NodeKind.Database && node.Ref.Database != server.MaintenanceDatabase)
            {
                continue;
            }

            try
            {
                await ObjectExplorer.GetPropertiesAsync(server, null, node.Ref);
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
                foreach (var child in await ObjectExplorer.GetChildrenAsync(server, null, node.Ref))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (Exception ex)
            {
                failures.Add($"children of {node.Ref.Id}: {ex.Message}");
            }
        }

        output.WriteLine($"{server.Provider}: {count} nodes; kinds: {string.Join(", ", kinds.Order())}");
        foreach (var failure in failures)
        {
            output.WriteLine("FAIL " + failure);
        }

        Assert.Empty(failures);
        Assert.Contains(NodeKind.Table, kinds);
        Assert.Contains(NodeKind.View, kinds);
        Assert.Contains(NodeKind.Column, kinds);
        Assert.Contains(NodeKind.Index, kinds);
        Assert.Contains(NodeKind.ForeignKey, kinds);
        Assert.Contains(NodeKind.Trigger, kinds);
        Assert.Contains(NodeKind.Procedure, kinds);
        Assert.Contains(NodeKind.Function, kinds);
    }

    private async Task DataAsync(ServerProfile server, string database)
    {
        await using var session = await ObjectExplorer.OpenSessionAsync(server, null, database);
        var catalog = await session.ReadCatalogAsync();
        var users = catalog.Relations.Single(r => r.Name == "users");
        Assert.True(users.Column("id")!.IsPrimaryKey);
        Assert.Contains(catalog.ForeignKeys, k => k.Table == "orders" && k.ReferencedTable == "users");
        Assert.Contains(catalog.Relations, r => r.Name == "big_orders" && r.Kind == TableKind.View);

        var schema = users.Schema;
        var table = new TableRef("users", schema);
        var page = await session.Data.QueryAsync(table, new DataQuery
        {
            PageSize = 2,
            IncludeTotalCount = true,
            WhereSql = "score >= 0",
            OrderBySql = "email DESC",
        });
        Assert.Equal(3, page.TotalCount);
        Assert.True(page.HasMore);
        Assert.Equal("c@x.io", page.Rows[0][page.Columns.ToList().IndexOf("email")]);

        var filtered = await session.Data.QueryAsync(table, new DataQuery { Filters = [FilterCondition.Contains("born", "1990")], IncludeTotalCount = true });
        Assert.Equal(1, filtered.TotalCount);

        var counts = await session.Writer.ApplyAsync(table,
        [
            RowChange.Insert(new Dictionary<string, object?> { ["id"] = SqlDefault.Value, ["email"] = "d@x.io", ["score"] = 7m, ["born"] = "2010-10-10", ["active"] = SqlDefault.Value }),
            RowChange.Update(new Dictionary<string, object?> { ["id"] = 2 }, new Dictionary<string, object?> { ["score"] = 6m, ["born"] = "1999-09-09" }),
        ]);
        Assert.Equal([1, 1], counts.ToArray());

        var failed = await Assert.ThrowsAsync<IntrospectionException>(() => session.Writer.ApplyAsync(table,
        [
            RowChange.Update(new Dictionary<string, object?> { ["id"] = 1 }, new Dictionary<string, object?> { ["score"] = 100m }),
            RowChange.Update(new Dictionary<string, object?> { ["id"] = 1 }, new Dictionary<string, object?> { ["score"] = -1m }),
        ]));
        output.WriteLine("expected failure: " + failed.Message);
        var score = (await session.Query.ExecuteAsync("SELECT score FROM users WHERE id = 1")).Rows[0][0];
        Assert.Equal(9007199254740993L, Convert.ToInt64(score, System.Globalization.CultureInfo.InvariantCulture));

        var batch = await session.Query.ExecuteAllAsync("SELECT COUNT(*) AS n FROM users; SELECT email FROM users WHERE id = 2");
        Assert.Equal(2, batch.Count);

        var explain = server.Provider == DatabaseProvider.SqlServer ? null : await session.Query.ExecuteAllAsync("EXPLAIN SELECT * FROM orders WHERE total > 10");
        if (explain is not null)
        {
            Assert.NotEmpty(explain[0].Rows);
        }

        const string text = "SELECT * FROM users u JOIN ";
        Assert.Contains(SqlCompletion.Complete(text, text.Length, catalog).Items, i => i.Kind == CompletionKind.Join);
    }

    private static async Task RunAsync(ServerProfile server, string? database, string sql)
    {
        await using var session = await ObjectExplorer.OpenSessionAsync(server, null, database);
        await session.Query.ExecuteAllAsync(sql);
    }
}
