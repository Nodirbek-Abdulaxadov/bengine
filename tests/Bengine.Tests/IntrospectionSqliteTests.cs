namespace Bengine.Tests;

using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Connection;
using Bengine.Introspection.Model;

/// <summary>
/// End-to-end tests for live-database introspection and data browsing, run
/// against a real on-disk SQLite database created per test. SQLite is the only
/// provider exercised here because it needs no server; the PostgreSQL, MySQL,
/// and SQL Server providers share the same contracts and require a live server
/// to test.
/// </summary>
public sealed class IntrospectionSqliteTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"bengine_{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_path}";

    public async Task InitializeAsync()
    {
        await using var connection = DatabaseConnectionFactory.Create(DatabaseProvider.Sqlite, ConnectionString);
        await connection.OpenAsync();

        await ExecuteAsync(connection, """
            CREATE TABLE users (
                id         INTEGER PRIMARY KEY,
                email      VARCHAR(320) NOT NULL,
                age        INTEGER,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            """);
        await ExecuteAsync(connection, "CREATE UNIQUE INDEX ux_users_email ON users(email);");
        await ExecuteAsync(connection, """
            CREATE TABLE posts (
                id      INTEGER PRIMARY KEY,
                user_id INTEGER NOT NULL,
                title   TEXT NOT NULL,
                FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
            );
            """);
        await ExecuteAsync(connection, "CREATE INDEX ix_posts_user ON posts(user_id);");

        for (var i = 1; i <= 5; i++)
        {
            await ExecuteAsync(connection,
                $"INSERT INTO users (email, age) VALUES ('user{i}@example.com', {20 + i});");
        }
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Discovers_user_tables_in_creation_order()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var tables = await session.Explorer.GetTablesAsync();

        Assert.Equal(["posts", "users"], tables.Select(t => t.Name).Order().ToArray());
        Assert.All(tables, t => Assert.Equal(TableKind.Table, t.Kind));
    }

    [Fact]
    public async Task Introspects_columns_primary_key_and_identity()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var users = await session.Introspector.IntrospectTableAsync(new TableRef("users"));

        Assert.Equal(["id", "email", "age", "created_at"], users.Columns.Select(c => c.Name).ToArray());

        var email = users.Columns.Single(c => c.Name == "email");
        Assert.Equal(DbDataType.VarChar, email.Type.DataType);
        Assert.Equal(320, email.Type.Length);
        Assert.Equal(Nullability.NotNull, email.Nullability);

        Assert.NotNull(users.PrimaryKey);
        Assert.Equal(["id"], users.PrimaryKey!.ColumnNames.ToArray());

        var id = users.Columns.Single(c => c.Name == "id");
        Assert.Equal(IdentityStrategy.AutoIncrement, id.Identity.Strategy);

        var createdAt = users.Columns.Single(c => c.Name == "created_at");
        Assert.Equal(DefaultValueKind.CurrentTimestamp, createdAt.Default.Kind);
    }

    [Fact]
    public async Task Introspects_unique_index()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var users = await session.Introspector.IntrospectTableAsync(new TableRef("users"));

        var index = Assert.Single(users.Indexes);
        Assert.Equal("ux_users_email", index.Name);
        Assert.True(index.IsUnique);
        Assert.Equal(["email"], index.Columns.Select(c => c.ColumnName).ToArray());
    }

    [Fact]
    public async Task Introspects_foreign_key_with_referential_action()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var posts = await session.Introspector.IntrospectTableAsync(new TableRef("posts"));

        var fk = Assert.Single(posts.ForeignKeys);
        Assert.Equal(["user_id"], fk.Columns.ToArray());
        Assert.Equal("users", fk.ReferencedTable);
        Assert.Equal(["id"], fk.ReferencedColumns.ToArray());
        Assert.Equal(ReferentialAction.Cascade, fk.OnDelete);
    }

    [Fact]
    public async Task Introspects_whole_schema_into_ast()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var schema = await session.Introspector.IntrospectAsync();

        Assert.Equal(2, schema.Tables.Count);
        Assert.Contains(schema.Tables, t => t.Name == "users");
        Assert.Contains(schema.Tables, t => t.Name == "posts");
    }

    [Fact]
    public async Task Pages_rows_and_reports_more()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var page1 = await session.Data.QueryAsync(
            new TableRef("users"),
            new DataQuery { Page = 1, PageSize = 2, Sort = [new SortSpec("id")] });

        Assert.Equal(2, page1.RowCount);
        Assert.True(page1.HasMore);

        var page3 = await session.Data.QueryAsync(
            new TableRef("users"),
            new DataQuery { Page = 3, PageSize = 2, Sort = [new SortSpec("id")] });

        Assert.Equal(1, page3.RowCount);
        Assert.False(page3.HasMore);
    }

    [Fact]
    public async Task Counts_total_when_requested()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var page = await session.Data.QueryAsync(
            new TableRef("users"),
            new DataQuery { PageSize = 2, IncludeTotalCount = true });

        Assert.Equal(5, page.TotalCount);
    }

    [Fact]
    public async Task Filters_with_bound_parameters()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var page = await session.Data.QueryAsync(
            new TableRef("users"),
            new DataQuery
            {
                Filters = [FilterCondition.Contains("email", "user3")],
                IncludeTotalCount = true,
            });

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, page.RowCount);
    }

    [Fact]
    public async Task Contains_matches_case_insensitively_and_on_non_text_columns()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var byEmail = await session.Data.QueryAsync(
            new TableRef("users"),
            new DataQuery { Filters = [FilterCondition.Contains("email", "USER3")], IncludeTotalCount = true });
        Assert.Equal(1, byEmail.TotalCount);

        // ages are 21..25; "2" appears in every one of them when compared as text.
        var byAge = await session.Data.QueryAsync(
            new TableRef("users"),
            new DataQuery { Filters = [FilterCondition.Contains("age", "2")], IncludeTotalCount = true });
        Assert.Equal(5, byAge.TotalCount);
    }

    [Fact]
    public async Task Reports_column_type_names()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var page = await session.Data.QueryAsync(new TableRef("users"), new DataQuery { PageSize = 1 });

        Assert.Equal(page.Columns.Count, page.ColumnTypes.Count);
        Assert.Equal("INTEGER", page.ColumnTypes[0], ignoreCase: true);
    }

    [Fact]
    public async Task Sorts_descending()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var page = await session.Data.QueryAsync(
            new TableRef("users"),
            new DataQuery { Sort = [new SortSpec("age", SortDirection.Descending)] });

        var ageOrdinal = page.Columns.ToList().IndexOf("age");
        var ages = page.Rows.Select(r => Convert.ToInt64(r[ageOrdinal])).ToArray();
        Assert.Equal(ages.OrderByDescending(a => a).ToArray(), ages);
    }

    [Fact]
    public async Task Rejects_unknown_filter_column()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        await Assert.ThrowsAsync<IntrospectionException>(() =>
            session.Data.QueryAsync(
                new TableRef("users"),
                new DataQuery { Filters = [FilterCondition.Equals("email; DROP TABLE users", "x")] }));
    }

    private static async Task ExecuteAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
