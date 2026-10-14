namespace Bengine.Tests;

using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Connection;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Tree;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

/// <summary>
/// Tests for the object explorer tree, the server store, and the pgAdmin import.
/// The tree walker expands every node and reads every node's properties, so a
/// single broken catalog query anywhere in the hierarchy fails the test. SQLite
/// always runs; PostgreSQL runs against a live server when the
/// <c>BENGINE_TEST_PG</c> environment variable holds
/// <c>host;port;database;user;password</c>.
/// </summary>
[Collection("live-postgres")]
public sealed class ObjectExplorerTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bengine_tree_{Guid.NewGuid():N}");

    public ObjectExplorerTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Walks_the_whole_sqlite_tree()
    {
        var file = Path.Combine(_dir, "shop.db");
        await using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE, name TEXT DEFAULT 'anon');
                CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE, total REAL);
                CREATE INDEX ix_orders_user ON orders(user_id);
                CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;
                CREATE TRIGGER trg_orders AFTER INSERT ON orders BEGIN SELECT 1; END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var server = new ServerProfile { Name = "local file", Provider = DatabaseProvider.Sqlite, FilePath = file };
        var visited = await WalkAsync(server, null, maxPerFolder: int.MaxValue);

        Assert.Contains(visited, n => n.Ref.Kind == NodeKind.Table && n.Label == "users");
        Assert.Contains(visited, n => n.Ref.Kind == NodeKind.View && n.Label == "big_orders");
        Assert.Contains(visited, n => n.Ref.Kind == NodeKind.ForeignKey);
        Assert.Contains(visited, n => n.Ref.Kind == NodeKind.UniqueConstraint);
        Assert.Contains(visited, n => n.Ref.Kind == NodeKind.Index && n.Label == "ix_orders_user");
        Assert.Contains(visited, n => n.Ref.Kind == NodeKind.Trigger && n.Label == "trg_orders");
        Assert.Contains(visited, n => n.Ref.Kind == NodeKind.Column && n.Label == "id" && n.Badge == "PK");

        var tablesFolder = visited.Single(n => n.Ref.Kind == NodeKind.TablesFolder);
        Assert.Equal(2, tablesFolder.Count);

        var users = visited.Single(n => n.Ref.Kind == NodeKind.Table && n.Label == "users");
        var properties = await ObjectExplorer.GetPropertiesAsync(server, null, users.Ref);
        Assert.Contains("CREATE TABLE users", properties.Sql);
    }

    [Fact]
    public async Task Walks_a_live_postgres_tree_when_configured()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_PG") is not { Length: > 0 } spec)
        {
            _output.WriteLine("BENGINE_TEST_PG is not set; skipping the live PostgreSQL walk.");
            return;
        }

        var parts = spec.Split(';');
        var server = new ServerProfile
        {
            Name = "live",
            Provider = DatabaseProvider.PostgreSql,
            Host = parts[0],
            Port = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
            MaintenanceDatabase = parts[2],
            Username = parts[3],
            Password = parts[4],
            SavePassword = true,
        };

        var visited = await WalkAsync(server, null, maxPerFolder: 3);

        Assert.Contains(visited, n => n.Ref.Kind == NodeKind.DatabasesFolder);
        Assert.Contains(visited, n => n.Ref.Kind == NodeKind.Schema);
        Assert.Contains(visited, n => n.Ref.Kind == NodeKind.Catalog);
        Assert.Contains(visited, n => n.Ref.Kind == NodeKind.Table);
        Assert.Contains(visited, n => n.Ref.Kind == NodeKind.Column);
        Assert.Contains(visited, n => n.Ref.Kind is NodeKind.LoginRole or NodeKind.GroupRole);
    }

    [Fact]
    public void Store_keeps_a_saved_password_when_an_edit_omits_it()
    {
        var store = new ServerStore(Path.Combine(_dir, "servers.json"));
        var saved = store.Upsert(new ServerProfile { Name = "pg", Provider = DatabaseProvider.PostgreSql, Host = "h", Password = "secret", SavePassword = true });

        var edited = store.Upsert(saved with { Name = "pg renamed", Password = null });

        Assert.Equal("secret", edited.Password);
        Assert.Equal("pg renamed", store.Get(saved.Id)!.Name);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
        }
    }

    [Fact]
    public void Store_drops_the_password_when_saving_is_turned_off()
    {
        var store = new ServerStore(Path.Combine(_dir, "servers.json"));
        var saved = store.Upsert(new ServerProfile { Name = "pg", Provider = DatabaseProvider.PostgreSql, Password = "secret", SavePassword = true });

        store.Upsert(saved with { SavePassword = false });

        Assert.Null(store.Get(saved.Id)!.Password);
        Assert.True(store.Remove(saved.Id));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Builds_a_postgres_connection_string_for_a_chosen_database()
    {
        var server = new ServerProfile
        {
            Name = "pg",
            Provider = DatabaseProvider.PostgreSql,
            Host = "db.example",
            Port = 5433,
            MaintenanceDatabase = "postgres",
            Username = "app",
            Password = "p;w'd",
            SavePassword = true,
            Options = "SSL Mode=Require",
        };

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionStringFactory.Build(server, "sales"));

        Assert.Equal("db.example", builder.Host);
        Assert.Equal(5433, builder.Port);
        Assert.Equal("sales", builder.Database);
        Assert.Equal("p;w'd", builder.Password);
        Assert.Equal(Npgsql.SslMode.Require, builder.SslMode);
    }

    [Fact]
    public void Imports_servers_from_a_pgadmin_database()
    {
        var file = Path.Combine(_dir, "pgadmin4.db");
        using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE servergroup (id INTEGER PRIMARY KEY, name TEXT);
                CREATE TABLE server (id INTEGER PRIMARY KEY, servergroup_id INTEGER, name TEXT, host TEXT, port INTEGER,
                                     maintenance_db TEXT, username TEXT, comment TEXT, password TEXT, connection_params TEXT);
                INSERT INTO servergroup VALUES (1, 'Servers');
                INSERT INTO server VALUES (1, 1, 'local-dev', 'localhost', 5433, 'app_db', 'postgres', NULL, 'ENCRYPTED', '{"sslmode":"prefer","connect_timeout":10}');
                """;
            command.ExecuteNonQuery();
        }

        var servers = PgAdminImporter.Read(file);

        var server = Assert.Single(servers);
        Assert.Equal("local-dev", server.Name);
        Assert.Equal("Servers", server.Group);
        Assert.Equal(5433, server.Port);
        Assert.Equal("app_db", server.MaintenanceDatabase);
        Assert.Null(server.Password);
        Assert.Contains("SSL Mode=Prefer", server.Options);
    }

    [Fact]
    public async Task Reports_a_wrong_password_as_an_authentication_error()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_PG") is not { Length: > 0 } spec)
        {
            return;
        }

        var parts = spec.Split(';');
        var server = new ServerProfile
        {
            Name = "live",
            Provider = DatabaseProvider.PostgreSql,
            Host = parts[0],
            Port = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
            MaintenanceDatabase = parts[2],
            Username = parts[3],
        };

        var ex = await Assert.ThrowsAsync<IntrospectionException>(() => ObjectExplorer.TestAsync(server, "definitely-wrong"));
        Assert.True(ObjectExplorer.IsAuthenticationError(ex));
    }

    [Fact]
    public async Task Binds_text_values_to_typed_postgres_columns_when_configured()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_PG") is not { Length: > 0 } spec)
        {
            return;
        }

        var parts = spec.Split(';');
        var server = new ServerProfile
        {
            Name = "live",
            Provider = DatabaseProvider.PostgreSql,
            Host = parts[0],
            Port = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
            MaintenanceDatabase = parts[2],
            Username = parts[3],
            Password = parts[4],
            SavePassword = true,
        };

        await using var session = await ObjectExplorer.OpenSessionAsync(server, null, null);
        var pgClass = new Bengine.Introspection.Model.TableRef("pg_class", "pg_catalog");

        // 'oid = text' is not an operator in PostgreSQL; an untyped parameter lets the
        // server read '1259' as an oid (pg_class's own row).
        var exact = await session.Data.QueryAsync(pgClass, new Bengine.Introspection.Model.DataQuery
        {
            Filters = [Bengine.Introspection.Model.FilterCondition.Equals("oid", "1259")],
            IncludeTotalCount = true,
        });
        Assert.Equal(1, exact.TotalCount);

        // A pattern filter works on a non-text column by comparing it as text.
        var pattern = await session.Data.QueryAsync(pgClass, new Bengine.Introspection.Model.DataQuery
        {
            Filters = [Bengine.Introspection.Model.FilterCondition.Contains("relname", "PG_CLASS")],
            IncludeTotalCount = true,
        });
        Assert.True(pattern.TotalCount >= 1);
        Assert.Contains("oid", exact.ColumnTypes);
    }

    // Breadth-first walk: expand every node and read every node's properties.
    private async Task<List<TreeNode>> WalkAsync(ServerProfile server, string? password, int maxPerFolder)
    {
        var root = new TreeNode { Ref = new NodeRef { Kind = NodeKind.Server }, Label = server.Name, HasChildren = true };
        var queue = new Queue<(TreeNode Node, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = new List<TreeNode>();
        var failures = new List<string>();

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            visited.Add(node);
            _output.WriteLine($"{new string(' ', depth * 2)}{node.Ref.Kind}: {node.Label}{(node.Count is { } c ? $" ({c})" : string.Empty)}{(node.Detail is { } d ? $"  [{d}]" : string.Empty)}");

            try
            {
                var properties = await ObjectExplorer.GetPropertiesAsync(server, password, node.Ref);
                Assert.False(string.IsNullOrEmpty(properties.Title));
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
                // Scratch databases of tests running in parallel (bengine_ui_*, bengine_bak_*, …) can vanish mid-walk.
                var children = (await ObjectExplorer.GetChildrenAsync(server, password, node.Ref))
                    .Where(c => !(c.Ref.Kind == NodeKind.Database && c.Ref.Name?.StartsWith("bengine_", StringComparison.Ordinal) == true))
                    .ToList();
                foreach (var child in node.IsFolder ? children.Take(maxPerFolder) : children)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (Exception ex)
            {
                failures.Add($"children of {node.Ref.Id}: {ex.Message}");
            }
        }

        _output.WriteLine($"visited {visited.Count} nodes, {failures.Count} failure(s)");
        foreach (var failure in failures)
        {
            _output.WriteLine("FAIL " + failure);
        }

        Assert.Empty(failures);
        return visited;
    }
}
