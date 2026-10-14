namespace Bengine.Tests;

using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Activity;
using Bengine.Introspection.Connection;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Tree;
using Xunit.Abstractions;

/// <summary>
/// Tests SSH tunnels, SSL options, and keyring-backed password storage.
/// SSH runs when <c>BENGINE_TEST_SSH</c> holds
/// <c>sshHost;sshPort;sshUser;sshPassword;dbHost;dbPort;database;dbUser;dbPassword</c>
/// (the database host as the bastion sees it); the keyring test runs against
/// the real Secret Service only when <c>BENGINE_TEST_KEYRING=1</c>, under a
/// test-only application tag, and removes what it stores.
/// </summary>
[Collection("live-postgres")]
public sealed class ConnectivityTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bengine_conn_").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Maps_ssl_options_to_each_driver()
    {
        var pg = new ServerProfile { Name = "pg", Provider = DatabaseProvider.PostgreSql, Host = "h", SslMode = "verify-full", SslRootCertificate = "/ca.pem", SslClientCertificate = "/c.pem", SslClientKey = "/k.pem" };
        var npgsql = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionStringFactory.Build(pg));
        Assert.Equal((Npgsql.SslMode.VerifyFull, "/ca.pem", "/c.pem", "/k.pem"), (npgsql.SslMode, npgsql.RootCertificate, npgsql.SslCertificate, npgsql.SslKey));

        var my = new MySqlConnector.MySqlConnectionStringBuilder(ConnectionStringFactory.Build(pg with { Provider = DatabaseProvider.MySql, SslMode = "require" }));
        Assert.Equal((MySqlConnector.MySqlSslMode.Required, "/ca.pem"), (my.SslMode, my.SslCa));

        var sql = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionStringFactory.Build(pg with { Provider = DatabaseProvider.SqlServer, SslMode = "require" }));
        Assert.Equal((Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory, true), (sql.Encrypt, sql.TrustServerCertificate));
    }

    [Fact]
    public async Task Requires_ssl_when_asked_on_live_postgres_when_configured()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_PG") is not { Length: > 0 } spec)
        {
            return;
        }

        var p = spec.Split(';');
        var server = new ServerProfile { Name = "pg", Provider = DatabaseProvider.PostgreSql, Host = p[0], Port = int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), MaintenanceDatabase = p[2], Username = p[3], Password = p[4], SavePassword = true };

        await ObjectExplorer.TestAsync(server with { SslMode = "disable" }, null);
        var refused = await Assert.ThrowsAsync<IntrospectionException>(() => ObjectExplorer.TestAsync(server with { SslMode = "require" }, null));
        output.WriteLine(refused.Message);
    }

    [Fact]
    public async Task Tunnels_through_ssh_when_configured()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_SSH") is not { Length: > 0 } spec)
        {
            output.WriteLine("BENGINE_TEST_SSH is not set; skipping.");
            return;
        }

        var p = spec.Split(';');
        SshTunnels.KnownHostsPath = Path.Combine(_dir, "known_hosts.json");
        var server = new ServerProfile
        {
            Name = "behind-bastion",
            Provider = DatabaseProvider.PostgreSql,
            Host = p[4],
            Port = int.Parse(p[5], System.Globalization.CultureInfo.InvariantCulture),
            MaintenanceDatabase = p[6],
            Username = p[7],
            Password = p[8],
            SavePassword = true,
            SshHost = p[0],
            SshPort = int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
            SshUser = p[2],
            SshPassword = p[3],
        };

        await using (var session = await ObjectExplorer.OpenSessionAsync(server, null, null))
        {
            var result = await session.Query.ExecuteAsync("SELECT inet_server_port(), current_database()");
            output.WriteLine($"via tunnel: port {result.Rows[0][0]}, database {result.Rows[0][1]}");
            Assert.Equal(p[6], result.Rows[0][1]);
        }

        var children = await ObjectExplorer.GetChildrenAsync(server, null, new NodeRef { Kind = NodeKind.Server });
        Assert.Contains(children, c => c.Ref.Kind == NodeKind.DatabasesFolder);
        Assert.Contains($"{p[0]}:{p[1]}", File.ReadAllText(SshTunnels.KnownHostsPath));

        // A host key that changed is refused.
        SshTunnels.Close(server.Id);
        File.WriteAllText(SshTunnels.KnownHostsPath, $"{{\"{p[0]}:{p[1]}\": \"not-the-key\"}}");
        var refused = await Assert.ThrowsAsync<IntrospectionException>(() => ObjectExplorer.TestAsync(server, null));
        Assert.Contains("SSH", refused.Message, StringComparison.Ordinal);
        SshTunnels.Close(server.Id);
    }

    [Fact]
    public void Keeps_saved_passwords_in_the_keyring_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_KEYRING") != "1" || !OperatingSystem.IsLinux())
        {
            output.WriteLine("BENGINE_TEST_KEYRING is not 1; skipping.");
            return;
        }

        var keyring = new SecretServiceStore($"bengine-test-{Guid.NewGuid():N}");
        var path = Path.Combine(_dir, "servers.json");
        var store = new ServerStore(path, keyring);
        var saved = store.Upsert(new ServerProfile { Name = "kr", Provider = DatabaseProvider.PostgreSql, Host = "h", Password = "s3cret", SshPassword = "ssh-pw", SavePassword = true });
        try
        {
            Assert.True(saved.PasswordInKeyring);
            Assert.DoesNotContain("s3cret", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal("s3cret", keyring.Get(saved.Id));

            var reloaded = new ServerStore(path, keyring).Get(saved.Id)!;
            Assert.Equal(("s3cret", "ssh-pw"), (reloaded.Password, reloaded.SshPassword));

            new ServerStore(path, keyring).Upsert(reloaded with { SavePassword = false });
            Assert.Null(keyring.Get(saved.Id));
        }
        finally
        {
            keyring.Delete(saved.Id);
            keyring.Delete(saved.Id + ":ssh");
        }
    }
}

/// <summary>Live tests of server activity: sessions, a blocked session with its blocker, locks, cancel, and terminate.</summary>
[Collection("live-postgres")]
public sealed class ServerActivityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Shows_blocking_sessions_and_ends_them_when_configured()
    {
        foreach (var (variable, provider) in new[] { ("BENGINE_TEST_PG", DatabaseProvider.PostgreSql), ("BENGINE_TEST_MYSQL", DatabaseProvider.MySql), ("BENGINE_TEST_MSSQL", DatabaseProvider.SqlServer) })
        {
            if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } spec)
            {
                continue;
            }

            var p = spec.Split(';');
            var server = new ServerProfile
            {
                Name = variable, Provider = provider, Host = p[0], Port = int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), MaintenanceDatabase = p[2],
                Username = p[3], Password = p[4], SavePassword = true, Options = provider == DatabaseProvider.SqlServer ? "TrustServerCertificate=True" : null,
            };
            var table = $"bengine_lock_{Guid.NewGuid():N}"[..24];
            await using var watcher = await ObjectExplorer.OpenSessionAsync(server, null, null);
            await watcher.Query.ExecuteAllAsync($"CREATE TABLE {table} (id int PRIMARY KEY, v int)");
            await watcher.Query.ExecuteAllAsync($"INSERT INTO {table} VALUES (1, 1)");
            try
            {
                await using var holder = await ObjectExplorer.OpenSessionAsync(server, null, null);
                await using var waiter = await ObjectExplorer.OpenSessionAsync(server, null, null);
                await holder.BeginTransactionAsync();
                await holder.Query.ExecuteAllAsync($"UPDATE {table} SET v = 2 WHERE id = 1");
                var blocked = waiter.Query.ExecuteAllAsync($"UPDATE {table} SET v = 3 WHERE id = 1");

                ActivitySession? victim = null;
                for (var i = 0; i < 50 && victim is null; i++)
                {
                    await Task.Delay(100);
                    var sessions = await ServerActivity.SessionsAsync(watcher);
                    victim = sessions.FirstOrDefault(s => s.Query?.Contains(table, StringComparison.OrdinalIgnoreCase) == true && s.BlockedBy is not null);
                }

                Assert.NotNull(victim);
                output.WriteLine($"{variable}: blocked session {victim!.Id} ({victim.State}, wait {victim.Wait}) blocked by {victim.BlockedBy}");
                var locks = await ServerActivity.LocksAsync(watcher);
                output.WriteLine($"{variable}: {locks.Count} lock(s), {locks.Count(l => !l.Granted)} awaited");
                Assert.Contains(locks, l => !l.Granted);

                await ServerActivity.CancelAsync(watcher, victim.Id);
                await Assert.ThrowsAnyAsync<Exception>(() => blocked);
                await holder.RollbackAsync();
            }
            finally
            {
                await watcher.Query.ExecuteAllAsync($"DROP TABLE {table}");
            }
        }
    }
}
