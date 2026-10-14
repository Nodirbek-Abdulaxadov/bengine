namespace Bengine.Introspection.Connection;

using System.Collections.Concurrent;
using System.Text.Json;
using Bengine.Introspection.Servers;
using Renci.SshNet;
using Renci.SshNet.Common;

/// <summary>
/// SSH tunnels for servers reachable only through a bastion host. A tunnel is
/// a local port on <c>127.0.0.1</c> forwarded over SSH to the database's host
/// and port; it is opened on first use, shared by every connection to that
/// server, and reopened if it drops. Host keys are trusted on first use and
/// remembered in <c>known_hosts.json</c> beside the server list; a changed key
/// is refused.
/// </summary>
public static class SshTunnels
{
    private static readonly ConcurrentDictionary<string, Tunnel> Open = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Gets or sets the file remembered host keys are kept in.</summary>
    public static string KnownHostsPath { get; set; } =
        Path.Combine(Path.GetDirectoryName(ServerStore.DefaultPath) ?? ".", "known_hosts.json");

    /// <summary>
    /// Returns the profile to connect with: unchanged when the server has no SSH
    /// host, otherwise pointing at the local end of its (opened) tunnel.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="cancellationToken">Cancels opening the tunnel.</param>
    /// <returns>The profile to build the connection string from.</returns>
    /// <exception cref="IntrospectionException">The tunnel could not be opened.</exception>
    public static async Task<ServerProfile> RouteAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(server.SshHost) || server.Provider == Enums.DatabaseProvider.Sqlite)
        {
            return server;
        }

        var target = server.Host ?? "localhost";
        var targetPort = server.Port ?? DefaultPort(server.Provider);
        var key = $"{server.Id}|{server.SshUser}@{server.SshHost}:{server.SshPort ?? 22}|{target}:{targetPort}";

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Open.TryGetValue(key, out var tunnel) || !tunnel.IsAlive)
            {
                tunnel?.Dispose();
                tunnel = await Task.Run(() => Tunnel.Start(server, target, targetPort), cancellationToken).ConfigureAwait(false);
                Open[key] = tunnel;
            }

            return server with { Host = "127.0.0.1", Port = (int)tunnel.LocalPort };
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Closes every tunnel of a server (on disconnect or edit).</summary>
    /// <param name="serverId">The server's id.</param>
    public static void Close(string serverId)
    {
        foreach (var key in Open.Keys.Where(k => k.StartsWith(serverId + "|", StringComparison.Ordinal)).ToList())
        {
            if (Open.TryRemove(key, out var tunnel))
            {
                tunnel.Dispose();
            }
        }
    }

    private static int DefaultPort(Enums.DatabaseProvider provider) => provider switch
    {
        Enums.DatabaseProvider.MySql => 3306,
        Enums.DatabaseProvider.SqlServer => 1433,
        Enums.DatabaseProvider.Oracle => 1521,
        Enums.DatabaseProvider.MongoDb => 27017,
        _ => 5432,
    };

    private sealed class Tunnel : IDisposable
    {
        private readonly SshClient _client;
        private readonly ForwardedPortLocal _port;

        private Tunnel(SshClient client, ForwardedPortLocal port)
        {
            _client = client;
            _port = port;
        }

        public uint LocalPort => _port.BoundPort;

        public bool IsAlive => _client.IsConnected && _port.IsStarted;

        public static Tunnel Start(ServerProfile server, string target, int targetPort)
        {
            var user = server.SshUser ?? Environment.UserName;
            var methods = new List<AuthenticationMethod>();
            try
            {
                if (!string.IsNullOrWhiteSpace(server.SshKeyFile))
                {
                    var file = server.SshKeyFile.StartsWith("~/", StringComparison.Ordinal)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), server.SshKeyFile[2..])
                        : server.SshKeyFile;
                    var key = string.IsNullOrEmpty(server.SshPassword)
                        ? new PrivateKeyFile(file)
                        : new PrivateKeyFile(file, server.SshPassword);
                    methods.Add(new PrivateKeyAuthenticationMethod(user, key));
                }
                else if (!string.IsNullOrEmpty(server.SshPassword))
                {
                    methods.Add(new PasswordAuthenticationMethod(user, server.SshPassword));
                    var interactive = new KeyboardInteractiveAuthenticationMethod(user);
                    interactive.AuthenticationPrompt += (_, e) =>
                    {
                        foreach (var prompt in e.Prompts)
                        {
                            prompt.Response = server.SshPassword;
                        }
                    };
                    methods.Add(interactive);
                }
                else
                {
                    throw new IntrospectionException($"SSH to {server.SshHost} needs a private key file or a password (server properties → SSH).");
                }

                var info = new ConnectionInfo(server.SshHost!, server.SshPort ?? 22, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
                var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                var endpoint = $"{server.SshHost}:{server.SshPort ?? 22}";
                client.HostKeyReceived += (_, e) => e.CanTrust = Trust(endpoint, e.FingerPrintSHA256);
                client.Connect();
                var port = new ForwardedPortLocal("127.0.0.1", 0, target, (uint)targetPort);
                client.AddForwardedPort(port);
                port.Start();
                return new Tunnel(client, port);
            }
            catch (Exception ex) when (ex is SshException or IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                throw new IntrospectionException($"Could not open the SSH tunnel through {server.SshHost}: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            try
            {
                _port.Stop();
                _client.Disconnect();
            }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
            {
                // Already gone.
            }

            _port.Dispose();
            _client.Dispose();
        }

        // Trust on first use; refuse a key that changed since.
        private static bool Trust(string endpoint, string fingerprint)
        {
            lock (Open)
            {
                var known = File.Exists(KnownHostsPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KnownHostsPath)) ?? new()
                    : new Dictionary<string, string>();
                if (known.TryGetValue(endpoint, out var remembered))
                {
                    return remembered == fingerprint;
                }

                known[endpoint] = fingerprint;
                Directory.CreateDirectory(Path.GetDirectoryName(KnownHostsPath)!);
                File.WriteAllText(KnownHostsPath, JsonSerializer.Serialize(known, new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
        }
    }
}
