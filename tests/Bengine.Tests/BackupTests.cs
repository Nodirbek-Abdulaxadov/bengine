namespace Bengine.Tests;

using System.Globalization;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Backup;
using Bengine.Introspection.Export;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Sql;
using Bengine.Introspection.Tree;
using Xunit.Abstractions;

/// <summary>
/// Dump and restore: the portable Bengine SQL dump replayed into an empty
/// database (keys, identities, and awkward values intact) on every provider,
/// the native tools' command lines, pg_dump/pg_restore and psql round trips,
/// SQL Server's server-side BACKUP/RESTORE, and SQLite's file copy.
/// </summary>
[Collection("live-postgres")]
public sealed class BackupTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bengine-backup-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Formats_dates_and_times_as_iso_literals()
    {
        Assert.Equal("'2026-10-14 13:45:07.25'", SqlLiterals.Format(new DateTime(2026, 10, 14, 13, 45, 7, 250), DatabaseProvider.MySql));
        Assert.Equal("'2026-10-14 13:45:07'", SqlLiterals.Format(new DateTime(2026, 10, 14, 13, 45, 7), DatabaseProvider.PostgreSql));
        Assert.Equal("N'2026-10-14'", SqlLiterals.Format(new DateTime(2026, 10, 14), DatabaseProvider.SqlServer));
        Assert.Equal("'2026-10-14'", SqlLiterals.Format(new DateOnly(2026, 10, 14), DatabaseProvider.Sqlite));
        Assert.Equal("'13:45:07'", SqlLiterals.Format(new TimeSpan(13, 45, 7), DatabaseProvider.MySql));
        Assert.Equal("'2026-10-14 13:45:07+05:00'", SqlLiterals.Format(new DateTimeOffset(2026, 10, 14, 13, 45, 7, TimeSpan.FromHours(5)), DatabaseProvider.PostgreSql));
        Assert.Equal("'it''s'", SqlLiterals.Format("it's", DatabaseProvider.PostgreSql));
    }

    [Fact]
    public async Task Builds_tool_commands_with_the_password_in_the_environment()
    {
        var pg = new ServerProfile { Name = "pg", Provider = DatabaseProvider.PostgreSql, Host = "db", Port = 6543, Username = "app", Password = "s3cret", SslMode = "verify-full" };
        if (NativeTools.Find(["pg_dump"]) is null)
        {
            output.WriteLine("pg_dump is not installed; checking only the missing-tool error.");
            await Assert.ThrowsAsync<IntrospectionException>(() => NativeTools.DumpCommandAsync(pg, null, "shop", new DumpRequest { OutputPath = "x" }));
            return;
        }

        var dump = await NativeTools.DumpCommandAsync(pg, null, "shop", new DumpRequest { OutputPath = "/tmp/a b.dump", Format = DumpFormat.Custom, Clean = true, NoOwner = true, Tables = ["public.users"] });
        output.WriteLine(dump.Display);
        Assert.Contains("--host=db", dump.Arguments);
        Assert.Contains("--port=6543", dump.Arguments);
        Assert.Contains("--format=c", dump.Arguments);
        Assert.Contains("--table=public.users", dump.Arguments);
        Assert.Contains("--if-exists", dump.Arguments);
        Assert.DoesNotContain(dump.Arguments, a => a.Contains("s3cret", StringComparison.Ordinal));
        Assert.DoesNotContain("s3cret", dump.Display, StringComparison.Ordinal);
        Assert.Equal("s3cret", dump.Environment["PGPASSWORD"]);
        Assert.Equal("verify-full", dump.Environment["PGSSLMODE"]);

        var archive = Path.Combine(_dir, "a.dump");
        await File.WriteAllBytesAsync(archive, "PGDMP\u0001\u000e"u8.ToArray());
        Assert.Equal(DumpFormat.Custom, NativeTools.DetectFormat(archive));
        var restore = await NativeTools.RestoreCommandAsync(pg, "typed", "shop", new RestoreRequest { InputPath = archive });
        Assert.Equal("pg_restore", Path.GetFileNameWithoutExtension(restore.FileName));
        Assert.Equal("typed", restore.Environment["PGPASSWORD"]);

        var script = Path.Combine(_dir, "a.sql");
        await File.WriteAllTextAsync(script, "SELECT 1;");
        var psql = await NativeTools.RestoreCommandAsync(pg, null, "shop", new RestoreRequest { InputPath = script });
        Assert.Equal("psql", Path.GetFileNameWithoutExtension(psql.FileName));
        Assert.Contains("--set=ON_ERROR_STOP=1", psql.Arguments);

        Assert.Contains("BACKUP DATABASE [we]]ird] TO DISK = N'C:\\b''s.bak'", NativeTools.SqlServerBackupSql("we]ird", "C:\\b's.bak"), StringComparison.Ordinal);
        Assert.Equal(3, NativeTools.SqlServerRestoreSql("shop", "/var/opt/mssql/data/shop.bak").Count);
    }

    [Fact]
    public async Task Dumps_and_restores_sqlite_with_a_script_and_a_file_copy()
    {
        var source = Sqlite("source.db");
        await using (var session = await ObjectExplorer.OpenSessionAsync(source, null, null))
        {
            await ScriptRunner.RunAsync(session, """
                CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE, score REAL, avatar BLOB, note TEXT);
                CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), total NUMERIC);
                CREATE INDEX ix_orders_user ON orders(user_id);
                CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;
                INSERT INTO users VALUES (1, 'a@x.io', 1.5, X'00FF10', 'it''s'), (2, 'b@x.io', NULL, NULL, 'back\slash'), (3, 'c@x.io', -2, X'', NULL);
                INSERT INTO orders VALUES (10, 1, 150), (11, 1, 20.5), (12, 3, NULL);
                """);
        }

        string script;
        await using (var session = await ObjectExplorer.OpenSessionAsync(source, null, null))
        {
            var writer = new StringWriter();
            var summary = await SqlDump.WriteAsync(session, null, writer, new SqlDumpOptions { RowsPerInsert = 2 });
            script = writer.ToString();
            Assert.Equal(2, summary.Tables);
            Assert.Equal(6, summary.Rows);
        }

        output.WriteLine(script);
        Assert.Contains("CREATE INDEX", script, StringComparison.Ordinal);
        Assert.Contains("CREATE VIEW", script, StringComparison.Ordinal);

        var copy = Sqlite("copy.db");
        await using (var session = await ObjectExplorer.OpenSessionAsync(copy, null, null))
        {
            var progress = await ScriptRunner.RunAsync(session, script, singleTransaction: true);
            Assert.Equal(0, progress.Failed);
        }

        await AssertSameAsync(source, null, copy, null, "SELECT id, email, score, hex(avatar), note FROM users ORDER BY id", "SELECT id, user_id, total FROM orders ORDER BY id", "SELECT COUNT(*) FROM big_orders");

        // VACUUM INTO, then the online backup API back into a fresh database.
        var backup = Path.Combine(_dir, "backup.db");
        await using (var session = await ObjectExplorer.OpenSessionAsync(source, null, null))
        {
            await session.Query.ExecuteAllAsync(NativeTools.SqliteBackupSql(backup));
        }

        var restored = Sqlite("restored.db");
        NativeTools.SqliteRestore(restored, backup);
        await AssertSameAsync(source, null, restored, null, "SELECT id, email, note FROM users ORDER BY id", "SELECT COUNT(*) FROM orders");
    }

    [Fact]
    public async Task Round_trips_a_bengine_dump_on_live_servers_when_configured()
    {
        foreach (var (variable, provider) in Providers())
        {
            if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } spec)
            {
                output.WriteLine($"{variable} is not set; skipping.");
                continue;
            }

            var admin = Admin(spec, provider);
            var source = $"bengine_bak_{Guid.NewGuid():N}"[..22];
            var target = $"bengine_bak_{Guid.NewGuid():N}"[..22];
            await ExecuteAsync(admin, null, $"CREATE DATABASE {source}");
            await ExecuteAsync(admin, null, $"CREATE DATABASE {target}");
            try
            {
                await ExecuteAsync(admin, source, Setup(provider));
                var writer = new StringWriter();
                await using (var session = await ObjectExplorer.OpenSessionAsync(admin, null, source))
                {
                    var summary = await SqlDump.WriteAsync(session, null, writer, new SqlDumpOptions { RowsPerInsert = 2 });
                    Assert.Equal(6, summary.Rows);
                }

                var script = writer.ToString();
                output.WriteLine($"=== {variable}\n{script}");
                await using (var session = await ObjectExplorer.OpenSessionAsync(admin, null, target))
                {
                    await ScriptRunner.RunAsync(session, script, singleTransaction: provider != DatabaseProvider.MySql);
                }

                await AssertSameAsync(admin, source, admin, target,
                    "SELECT id, email, score, born, note FROM users ORDER BY id",
                    "SELECT id, user_id, total FROM orders ORDER BY id",
                    "SELECT COUNT(*) FROM big_orders",
                    "SELECT COUNT(*) FROM active_buyers");

                // Identities continue after the loaded keys, and the foreign key was restored.
                await ExecuteAsync(admin, target, "INSERT INTO users (email) VALUES ('new@x.io')");
                Assert.Equal("4", await ScalarAsync(admin, target, "SELECT MAX(id) FROM users"));
                await Assert.ThrowsAsync<IntrospectionException>(() => ExecuteAsync(admin, target, "INSERT INTO orders (user_id, total) VALUES (999, 1)"));
            }
            finally
            {
                await DropAsync(admin, provider, source);
                await DropAsync(admin, provider, target);
            }
        }
    }

    [Fact]
    public async Task Round_trips_pg_dump_archives_and_plain_scripts_when_configured()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_PG") is not { Length: > 0 } spec || NativeTools.Find(["pg_dump"]) is null)
        {
            output.WriteLine("BENGINE_TEST_PG or pg_dump is missing; skipping.");
            return;
        }

        var admin = Admin(spec, DatabaseProvider.PostgreSql);
        var source = $"bengine_bak_{Guid.NewGuid():N}"[..22];
        var targets = new[] { $"bengine_bak_{Guid.NewGuid():N}"[..22], $"bengine_bak_{Guid.NewGuid():N}"[..22] };
        await ExecuteAsync(admin, null, $"CREATE DATABASE {source}");
        foreach (var target in targets)
        {
            await ExecuteAsync(admin, null, $"CREATE DATABASE {target}");
        }

        try
        {
            await ExecuteAsync(admin, source, Setup(DatabaseProvider.PostgreSql) + "\nCREATE FUNCTION twice(x int) RETURNS int LANGUAGE sql AS 'SELECT x * 2';");
            var log = new List<string>();
            foreach (var (format, target) in new[] { (DumpFormat.Custom, targets[0]), (DumpFormat.Plain, targets[1]) })
            {
                var file = Path.Combine(_dir, $"dump.{format}");
                var dump = await NativeTools.DumpCommandAsync(admin, null, source, new DumpRequest { OutputPath = file, Format = format, NoOwner = true });
                Assert.Equal(0, await NativeTools.RunAsync(dump, log.Add));
                Assert.Equal(format, NativeTools.DetectFormat(file));
                var restore = await NativeTools.RestoreCommandAsync(admin, null, target, new RestoreRequest { InputPath = file, NoOwner = true });
                var code = await NativeTools.RunAsync(restore, log.Add);
                output.WriteLine($"{format}: {restore.Display} → {code}");
                Assert.Equal(0, code);
                await AssertSameAsync(admin, source, admin, target, "SELECT id, email, note FROM users ORDER BY id", "SELECT twice(21)");
            }

            // A failing restore reports a non-zero exit code and the error text.
            var bad = Path.Combine(_dir, "bad.sql");
            await File.WriteAllTextAsync(bad, "CREATE TABLE users (id int);");
            log.Clear();
            var failing = await NativeTools.RestoreCommandAsync(admin, null, targets[1], new RestoreRequest { InputPath = bad });
            Assert.NotEqual(0, await NativeTools.RunAsync(failing, log.Add));
            Assert.Contains(log, l => l.Contains("already exists", StringComparison.Ordinal));
        }
        finally
        {
            await DropAsync(admin, DatabaseProvider.PostgreSql, source);
            foreach (var target in targets)
            {
                await DropAsync(admin, DatabaseProvider.PostgreSql, target);
            }
        }
    }

    [Fact]
    public async Task Backs_up_and_restores_sql_server_on_the_server_when_configured()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_MSSQL") is not { Length: > 0 } spec)
        {
            output.WriteLine("BENGINE_TEST_MSSQL is not set; skipping.");
            return;
        }

        var admin = Admin(spec, DatabaseProvider.SqlServer);
        var database = $"bengine_bak_{Guid.NewGuid():N}"[..22];
        var file = $"/var/opt/mssql/data/{database}.bak";
        await ExecuteAsync(admin, null, $"CREATE DATABASE {database}");
        try
        {
            await ExecuteAsync(admin, database, Setup(DatabaseProvider.SqlServer));
            var messages = new List<string>();
            await using (var session = await ObjectExplorer.OpenSessionAsync(admin, null, null))
            {
                session.ServerMessage += messages.Add;
                await session.Query.ExecuteAllAsync(NativeTools.SqlServerBackupSql(database, file));
            }

            output.WriteLine(string.Join('\n', messages));
            Assert.Contains(messages, m => m.Contains("BACKUP DATABASE successfully", StringComparison.Ordinal));

            await ExecuteAsync(admin, database, "DELETE FROM orders; DELETE FROM users");
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            await using (var session = await ObjectExplorer.OpenSessionAsync(admin, null, null))
            {
                foreach (var statement in NativeTools.SqlServerRestoreSql(database, file))
                {
                    await session.Query.ExecuteAllAsync(statement);
                }
            }

            Assert.Equal("3", await ScalarAsync(admin, database, "SELECT COUNT(*) FROM users"));
        }
        finally
        {
            await DropAsync(admin, DatabaseProvider.SqlServer, database);
        }
    }

    private static IEnumerable<(string Variable, DatabaseProvider Provider)> Providers() =>
    [
        ("BENGINE_TEST_PG", DatabaseProvider.PostgreSql),
        ("BENGINE_TEST_MYSQL", DatabaseProvider.MySql),
        ("BENGINE_TEST_MARIADB", DatabaseProvider.MySql),
        ("BENGINE_TEST_MSSQL", DatabaseProvider.SqlServer),
    ];

    private static string Setup(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.PostgreSql => """
            CREATE TABLE users (id int GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, email varchar(120) NOT NULL UNIQUE, score numeric(12,2), born date, note text, avatar bytea);
            CREATE TABLE orders (id serial PRIMARY KEY, user_id int NOT NULL REFERENCES users(id), total numeric(10,2));
            CREATE INDEX ix_orders_total ON orders(total);
            CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;
            CREATE VIEW active_buyers AS SELECT u.* FROM users u WHERE u.id IN (SELECT user_id FROM big_orders);
            INSERT INTO users (email, score, born, note, avatar) VALUES ('a@x.io', 1.5, '1990-01-02', 'it''s', '\x00ff'), ('b@x.io', NULL, NULL, 'back\slash', NULL), ('c@x.io', -2, '2001-05-06', NULL, '\x');
            INSERT INTO orders (user_id, total) VALUES (1, 150), (1, 20.5), (3, NULL);
            """,
        DatabaseProvider.MySql => """
            CREATE TABLE users (id int AUTO_INCREMENT PRIMARY KEY, email varchar(120) NOT NULL UNIQUE, score decimal(12,2), born date, note text, avatar blob);
            CREATE TABLE orders (id int AUTO_INCREMENT PRIMARY KEY, user_id int NOT NULL, total decimal(10,2), FOREIGN KEY (user_id) REFERENCES users(id));
            CREATE INDEX ix_orders_total ON orders(total);
            CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;
            CREATE VIEW active_buyers AS SELECT u.* FROM users u WHERE u.id IN (SELECT user_id FROM big_orders);
            INSERT INTO users (email, score, born, note, avatar) VALUES ('a@x.io', 1.5, '1990-01-02', 'it''s', X'00FF'), ('b@x.io', NULL, NULL, 'back\\slash', NULL), ('c@x.io', -2, '2001-05-06', NULL, X'');
            INSERT INTO orders (user_id, total) VALUES (1, 150), (1, 20.5), (3, NULL);
            """,
        _ => """
            CREATE TABLE users (id int IDENTITY(1,1) PRIMARY KEY, email nvarchar(120) NOT NULL UNIQUE, score decimal(12,2), born date, note nvarchar(max), avatar varbinary(max));
            CREATE TABLE orders (id int IDENTITY(1,1) PRIMARY KEY, user_id int NOT NULL REFERENCES users(id), total decimal(10,2));
            CREATE INDEX ix_orders_total ON orders(total);
            GO
            CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;
            GO
            CREATE VIEW active_buyers AS SELECT u.* FROM users u WHERE u.id IN (SELECT user_id FROM big_orders);
            GO
            INSERT INTO users (email, score, born, note, avatar) VALUES (N'a@x.io', 1.5, '1990-01-02', N'it''s', 0x00FF), (N'b@x.io', NULL, NULL, N'back\slash', NULL), (N'c@x.io', -2, '2001-05-06', NULL, 0x);
            INSERT INTO orders (user_id, total) VALUES (1, 150), (1, 20.5), (3, NULL);
            """,
    };

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
        foreach (var statement in SqlScript.Split(sql, server.Provider))
        {
            await session.Query.ExecuteAllAsync(statement.Text);
        }
    }

    private static async Task<string?> ScalarAsync(ServerProfile server, string? database, string sql)
    {
        await using var session = await ObjectExplorer.OpenSessionAsync(server, null, database);
        return Convert.ToString((await session.Query.ExecuteAsync(sql)).Rows[0][0], CultureInfo.InvariantCulture);
    }

    private async Task AssertSameAsync(ServerProfile a, string? aDatabase, ServerProfile b, string? bDatabase, params string[] queries)
    {
        await using var left = await ObjectExplorer.OpenSessionAsync(a, null, aDatabase);
        await using var right = await ObjectExplorer.OpenSessionAsync(b, null, bDatabase);
        foreach (var query in queries)
        {
            static string Rows(Bengine.Introspection.Model.QueryResult r) =>
                string.Join('\n', r.Rows.Select(row => string.Join(" | ", row.Select(v => v switch
                {
                    null => "NULL",
                    byte[] bytes => Convert.ToHexString(bytes),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => v.ToString(),
                }))));
            var expected = Rows(await left.Query.ExecuteAsync(query));
            var actual = Rows(await right.Query.ExecuteAsync(query));
            output.WriteLine($"{query}\n{actual}");
            Assert.Equal(expected, actual);
        }
    }

    private static async Task DropAsync(ServerProfile admin, DatabaseProvider provider, string database)
    {
        Npgsql.NpgsqlConnection.ClearAllPools();
        MySqlConnector.MySqlConnection.ClearAllPools();
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await using var session = await ObjectExplorer.OpenSessionAsync(admin, null, null);
        if (provider == DatabaseProvider.SqlServer)
        {
            await session.Query.ExecuteAllAsync($"IF DB_ID('{database}') IS NOT NULL ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        }

        await session.Query.ExecuteAllAsync(provider == DatabaseProvider.PostgreSql ? $"DROP DATABASE IF EXISTS {database} WITH (FORCE)" : $"DROP DATABASE IF EXISTS {database}");
    }
}
