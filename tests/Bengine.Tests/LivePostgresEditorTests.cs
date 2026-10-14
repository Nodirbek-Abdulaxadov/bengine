namespace Bengine.Tests;

using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Model;
using Bengine.Introspection.Sql;
using Npgsql;
using Xunit.Abstractions;

/// <summary>
/// Live PostgreSQL tests of the editor engine: catalog reading, completion over
/// a real catalog, manual transactions, staged data changes with server-coerced
/// types, notices, error positions, read-only sessions, and plans. They run when
/// <c>BENGINE_TEST_PG</c> holds <c>host;port;database;user;password</c>, inside a
/// scratch database created for the run and dropped afterwards, so the
/// configured database is never written to.
/// </summary>
[Collection("live-postgres")]
public sealed class LivePostgresEditorTests(ITestOutputHelper output) : IAsyncLifetime
{
    private string? _admin;
    private string? _scratch;
    private string _database = $"bengine_scratch_{Guid.NewGuid():N}"[..32];

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_PG") is not { Length: > 0 } spec)
        {
            return;
        }

        var p = spec.Split(';');
        _admin = new NpgsqlConnectionStringBuilder { Host = p[0], Port = int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), Database = p[2], Username = p[3], Password = p[4] }.ConnectionString;
        _scratch = new NpgsqlConnectionStringBuilder(_admin) { Database = _database }.ConnectionString;
        await Admin($"CREATE DATABASE {_database}");
        await using var connection = new NpgsqlConnection(_scratch);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE SCHEMA app;
            CREATE TABLE app.users (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), email text NOT NULL UNIQUE, created_at timestamptz NOT NULL DEFAULT now(), score bigint);
            CREATE TABLE app.orders (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, user_id uuid NOT NULL REFERENCES app.users(id), total numeric(10,2));
            CREATE VIEW app.big_orders AS SELECT * FROM app.orders WHERE total > 100;
            CREATE FUNCTION app.double_it(x int) RETURNS int LANGUAGE sql AS $$ SELECT x * 2 $$;
            INSERT INTO app.users (id, email, score) VALUES ('11111111-1111-1111-1111-111111111111', 'a@x.io', 9007199254740993);
            INSERT INTO app.orders (user_id, total) VALUES ('11111111-1111-1111-1111-111111111111', 150);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_admin is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await Admin($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)");
    }

    [Fact]
    public async Task Reads_the_catalog_and_completes_over_it()
    {
        if (_scratch is null)
        {
            output.WriteLine("BENGINE_TEST_PG is not set; skipping.");
            return;
        }

        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.PostgreSql, _scratch);
        var catalog = await session.ReadCatalogAsync();

        Assert.Equal("public", catalog.DefaultSchema);
        Assert.Contains("app", catalog.Schemas);
        var users = catalog.Find("app", "users")!;
        Assert.Equal(["id", "email", "created_at", "score"], users.Columns.Select(c => c.Name).ToArray());
        Assert.True(users.Column("id")!.IsPrimaryKey);
        Assert.Equal("uuid", users.Column("id")!.Type);
        Assert.True(catalog.Find("app", "orders")!.Column("id")!.HasDefault);
        Assert.Equal(TableKind.View, catalog.Find("app", "big_orders")!.Kind);
        var key = Assert.Single(catalog.ForeignKeys);
        Assert.Equal(("orders", "users", "user_id", "id"), (key.Table, key.ReferencedTable, key.Columns[0], key.ReferencedColumns[0]));
        Assert.Contains(catalog.Routines, r => r.Name == "double_it");

        const string text = "SELECT * FROM app.users u JOIN ";
        var completion = SqlCompletion.Complete(text, text.Length, catalog);
        Assert.Contains(completion.Items, i => i.InsertText == "app.orders o ON o.user_id = u.id");
    }

    [Fact]
    public async Task Runs_manual_transactions_notices_and_reports_error_positions()
    {
        if (_scratch is null)
        {
            return;
        }

        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.PostgreSql, _scratch);
        var notices = new List<string>();
        session.ServerMessage += notices.Add;

        await session.Query.ExecuteAllAsync("DO $$ BEGIN RAISE NOTICE 'hello from plpgsql'; END $$");
        Assert.Contains(notices, n => n.Contains("hello from plpgsql", StringComparison.Ordinal));

        await session.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await session.Query.ExecuteAllAsync("UPDATE app.users SET score = 1");
        await session.RollbackAsync();
        var score = (await session.Query.ExecuteAsync("SELECT score FROM app.users")).Rows[0][0];
        Assert.Equal(9007199254740993L, score);

        var error = await Assert.ThrowsAsync<IntrospectionException>(() => session.Query.ExecuteAllAsync("SELECT emial FROM app.users"));
        Assert.Equal(8, error.Position);

        var plan = await session.Query.ExecuteAllAsync("EXPLAIN SELECT * FROM app.orders WHERE user_id = '11111111-1111-1111-1111-111111111111'");
        Assert.Contains("Seq Scan", string.Join("\n", plan[0].Rows.Select(r => r[0])), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applies_staged_changes_with_server_coerced_types_and_refuses_writes_when_read_only()
    {
        if (_scratch is null)
        {
            return;
        }

        await using (var session = await DatabaseSession.OpenAsync(DatabaseProvider.PostgreSql, _scratch))
        {
            var table = new TableRef("users", "app");
            var counts = await session.Writer.ApplyAsync(table,
            [
                RowChange.Insert(new Dictionary<string, object?> { ["id"] = SqlDefault.Value, ["email"] = "b@x.io", ["created_at"] = "2026-01-02 03:04:05+00", ["score"] = "42" }),
                RowChange.Update(new Dictionary<string, object?> { ["id"] = "11111111-1111-1111-1111-111111111111" }, new Dictionary<string, object?> { ["score"] = SqlDefault.Value }),
            ]);
            Assert.Equal([1, 1], counts.ToArray());

            var rows = await session.Query.ExecuteAsync("SELECT email, score, created_at::date::text FROM app.users ORDER BY email");
            Assert.Equal(("a@x.io", null, "b@x.io", 42L, "2026-01-02"), ((string)rows.Rows[0][0]!, rows.Rows[0][1], (string)rows.Rows[1][0]!, rows.Rows[1][1], (string)rows.Rows[1][2]!));

            var failed = await Assert.ThrowsAsync<IntrospectionException>(() => session.Writer.ApplyAsync(table,
            [
                RowChange.Update(new Dictionary<string, object?> { ["email"] = "b@x.io" }, new Dictionary<string, object?> { ["score"] = "7" }),
                RowChange.Delete(new Dictionary<string, object?> { ["email"] = "nobody@x.io" }),
            ]));
            Assert.Contains("matched 0 rows", failed.Message, StringComparison.Ordinal);
            Assert.Equal(42L, (await session.Query.ExecuteAsync("SELECT score FROM app.users WHERE email = 'b@x.io'")).Rows[0][0]);
        }

        await using var readOnly = await DatabaseSession.OpenAsync(DatabaseProvider.PostgreSql, _scratch, new SessionOptions { ReadOnly = true });
        var refused = await Assert.ThrowsAsync<IntrospectionException>(() => readOnly.Query.ExecuteAllAsync("DELETE FROM app.orders"));
        Assert.Contains("read-only", refused.Message, StringComparison.Ordinal);
    }

    private async Task Admin(string sql)
    {
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
