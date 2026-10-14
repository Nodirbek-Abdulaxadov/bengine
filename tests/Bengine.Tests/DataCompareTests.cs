namespace Bengine.Tests;

using System.Globalization;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Backup;
using Bengine.Introspection.Compare;
using Bengine.Introspection.Model;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Sql;
using Bengine.Introspection.Tree;
using Xunit.Abstractions;

/// <summary>
/// Data compare: values compare by meaning across drivers, differing rows are
/// found by key in SQLite and across live servers (PostgreSQL against MySQL
/// and SQL Server), and the sync script makes the target identical.
/// </summary>
[Collection("live-postgres")]
public sealed class DataCompareTests(ITestOutputHelper output) : IDisposable
{
    private const string Rows = """
        INSERT INTO items (id, name, price, made, active) VALUES (1, 'one', 1.50, '2024-01-02', 1), (2, 'two', 2, '2024-02-03', 0), (3, 'three', NULL, NULL, 1), (4, 'it''s', 4.25, '2024-04-05', 1);
        """;

    private const string Changes = """
        UPDATE items SET price = 9.99 WHERE id = 2;
        UPDATE items SET name = 'THREE', made = '2025-01-01' WHERE id = 3;
        DELETE FROM items WHERE id = 4;
        INSERT INTO items (id, name, price, made, active) VALUES (5, 'five', 5, '2024-05-06', 0);
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("bengine-datacmp-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Compares_values_by_meaning()
    {
        Assert.True(DataComparer.ValuesEqual(1.5m, "1.50"));
        Assert.True(DataComparer.ValuesEqual(2L, 2));
        Assert.True(DataComparer.ValuesEqual(true, "t"));
        Assert.True(DataComparer.ValuesEqual(true, 1));
        Assert.True(DataComparer.ValuesEqual(new DateTime(2024, 1, 2), "2024-01-02"));
        Assert.True(DataComparer.ValuesEqual(new DateTime(2024, 1, 2), "2024-01-02 00:00:00"));
        Assert.True(DataComparer.ValuesEqual(new byte[] { 0, 255 }, "\\x00ff"));
        Assert.False(DataComparer.ValuesEqual("007", "7"));
        Assert.False(DataComparer.ValuesEqual(null, ""));
        Assert.False(DataComparer.ValuesEqual(1.5m, 1.51m));
    }

    [Fact]
    public async Task Finds_differences_and_syncs_sqlite_tables()
    {
        var a = Sqlite("a.db");
        var b = Sqlite("b.db");
        const string Create = "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL, price NUMERIC, made TEXT, active INTEGER, extra TEXT);";
        await ExecuteAsync(a, null, Create + Rows);
        await ExecuteAsync(b, null, Create.Replace(", extra TEXT", string.Empty, StringComparison.Ordinal) + Rows + Changes);
        await AssertSyncAsync(a, null, b, null, new TableRef("items", null));
    }

    [Fact]
    public async Task Compares_and_syncs_across_live_providers_when_configured()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_PG") is not { Length: > 0 } pgSpec)
        {
            output.WriteLine("BENGINE_TEST_PG is not set; skipping.");
            return;
        }

        var pg = Admin(pgSpec, DatabaseProvider.PostgreSql);
        var source = $"bengine_cmp_{Guid.NewGuid():N}"[..22];
        await ExecuteAsync(pg, null, $"CREATE DATABASE {source}");
        try
        {
            await ExecuteAsync(pg, source, "CREATE TABLE items (id int PRIMARY KEY, name varchar(40) NOT NULL, price numeric(10,2), made date, active boolean);"
                + Rows.Replace("1),", "true),", StringComparison.Ordinal).Replace("0),", "false),", StringComparison.Ordinal).Replace("1);", "true);", StringComparison.Ordinal));

            // Same provider, another database.
            var copy = $"bengine_cmp_{Guid.NewGuid():N}"[..22];
            await ExecuteAsync(pg, null, $"CREATE DATABASE {copy}");
            try
            {
                await ExecuteAsync(pg, copy, "CREATE TABLE items (id int PRIMARY KEY, name varchar(40) NOT NULL, price numeric(10,2), made date, active boolean);"
                    + Rows.Replace("1),", "true),", StringComparison.Ordinal).Replace("0),", "false),", StringComparison.Ordinal).Replace("1);", "true);", StringComparison.Ordinal)
                    + Changes.Replace("1, 'five', 5, '2024-05-06', 0", "5, 'five', 5, '2024-05-06', false", StringComparison.Ordinal).Replace("0);", "false);", StringComparison.Ordinal));
                await AssertSyncAsync(pg, source, pg, copy, new TableRef("items", "public"));
            }
            finally
            {
                await DropAsync(pg, copy);
            }

            // Other providers as targets.
            foreach (var (variable, provider, create) in new[]
            {
                ("BENGINE_TEST_MYSQL", DatabaseProvider.MySql, "CREATE TABLE items (id int PRIMARY KEY, name varchar(40) NOT NULL, price decimal(10,2), made date, active tinyint(1));"),
                ("BENGINE_TEST_MSSQL", DatabaseProvider.SqlServer, "CREATE TABLE items (id int IDENTITY(1,1) PRIMARY KEY, name nvarchar(40) NOT NULL, price decimal(10,2), made date, active bit);"),
            })
            {
                if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } spec)
                {
                    continue;
                }

                var server = Admin(spec, provider);
                var target = $"bengine_cmp_{Guid.NewGuid():N}"[..22];
                await ExecuteAsync(server, null, $"CREATE DATABASE {target}");
                try
                {
                    var identity = provider == DatabaseProvider.SqlServer;
                    await ExecuteAsync(server, target, create + (identity ? "SET IDENTITY_INSERT items ON;" : string.Empty) + Rows + Changes + (identity ? "SET IDENTITY_INSERT items OFF;" : string.Empty));
                    await AssertSyncAsync(pg, source, server, target, new TableRef("items", provider == DatabaseProvider.SqlServer ? "dbo" : target), new TableRef("items", "public"));
                }
                finally
                {
                    await DropAsync(server, target);
                }
            }
        }
        finally
        {
            await DropAsync(pg, source);
        }
    }

    private async Task AssertSyncAsync(ServerProfile sourceServer, string? sourceDb, ServerProfile targetServer, string? targetDb, TableRef targetTable, TableRef? sourceTable = null)
    {
        sourceTable ??= targetTable;
        await using var source = await ObjectExplorer.OpenSessionAsync(sourceServer, null, sourceDb);
        await using var target = await ObjectExplorer.OpenSessionAsync(targetServer, null, targetDb);
        var result = await DataComparer.CompareAsync(source, sourceTable, target, targetTable);
        output.WriteLine($"{sourceServer.Provider} → {targetServer.Provider}: same {result.Same}, different {result.Different}, only source {result.OnlyInSource}, only target {result.OnlyInTarget}");
        Assert.Equal(1, result.Same);
        Assert.Equal(2, result.Different);
        Assert.Equal(1, result.OnlyInSource);
        Assert.Equal(1, result.OnlyInTarget);
        Assert.Equal(["id"], result.KeyColumns);
        var three = result.Differences.Single(d => d.Kind == RowDiffKind.Different && Convert.ToInt64(d.Key[0], CultureInfo.InvariantCulture) == 3);
        Assert.Equal(["name", "made"], three.Changed.Order().Select(i => result.Columns[i]));

        var changes = result.SyncChanges();
        var script = DataComparer.SyncScript(targetServer.Provider, targetTable, result, changes);
        output.WriteLine(script);
        Assert.Contains("DELETE FROM", script, StringComparison.Ordinal);
        Assert.Contains("UPDATE", script, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO", script, StringComparison.Ordinal);

        Assert.Equal(4, await DataComparer.ApplyAsync(target, targetTable, result, changes));
        var again = await DataComparer.CompareAsync(source, sourceTable, target, targetTable);
        Assert.True(again.Identical, $"still {again.Different} different, {again.OnlyInSource} + {again.OnlyInTarget} missing");
        Assert.Equal(4, again.Same);
    }

    private ServerProfile Sqlite(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, []);
        return new ServerProfile { Name = name, Provider = DatabaseProvider.Sqlite, FilePath = path };
    }

    private static ServerProfile Admin(string spec, DatabaseProvider provider)
    {
        var p = spec.Split(';');
        return new ServerProfile
        {
            Name = "admin", Provider = provider, Host = p[0], Port = int.Parse(p[1], CultureInfo.InvariantCulture), MaintenanceDatabase = p[2],
            Username = p[3], Password = p[4], SavePassword = true, Options = provider == DatabaseProvider.SqlServer ? "TrustServerCertificate=True" : null,
        };
    }

    private static async Task ExecuteAsync(ServerProfile server, string? database, string sql)
    {
        await using var session = await ObjectExplorer.OpenSessionAsync(server, null, database);
        await ScriptRunner.RunAsync(session, sql);
    }

    private static async Task DropAsync(ServerProfile admin, string database)
    {
        Npgsql.NpgsqlConnection.ClearAllPools();
        MySqlConnector.MySqlConnection.ClearAllPools();
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await using var session = await ObjectExplorer.OpenSessionAsync(admin, null, null);
        if (admin.Provider == DatabaseProvider.SqlServer)
        {
            await session.Query.ExecuteAllAsync($"IF DB_ID('{database}') IS NOT NULL ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        }

        await session.Query.ExecuteAllAsync(admin.Provider == DatabaseProvider.PostgreSql ? $"DROP DATABASE IF EXISTS {database} WITH (FORCE)" : $"DROP DATABASE IF EXISTS {database}");
    }
}
