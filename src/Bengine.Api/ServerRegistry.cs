namespace Bengine.Api;

using System.Collections.Concurrent;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Connection;
using Bengine.Introspection.Servers;

/// <summary>
/// The API's view of registered servers: the persistent <see cref="ServerStore"/>
/// plus, for servers whose password is not saved, the password typed at connect
/// time, held in memory for the life of the process (as pgAdmin does). Also
/// resolves a request's target — either a registered server and database, or a
/// raw provider and connection string — to a connection.
/// </summary>
public sealed class ServerRegistry
{
    private readonly ConcurrentDictionary<string, string> _passwords = new();
    private readonly ConcurrentDictionary<string, bool> _connected = new();

    /// <summary>Creates the registry over a store.</summary>
    /// <param name="store">The persistent server list.</param>
    public ServerRegistry(ServerStore store) => Store = store;

    /// <summary>Gets the persistent server list.</summary>
    public ServerStore Store { get; }

    /// <summary>Gets the password to use for a server: one typed this session, else the saved one.</summary>
    /// <param name="server">The server.</param>
    /// <returns>The password, or <c>null</c>.</returns>
    public string? PasswordFor(ServerProfile server) =>
        _passwords.TryGetValue(server.Id, out var typed) ? typed : server.Password;

    /// <summary>Records a successful connection, remembering a typed password for this session.</summary>
    /// <param name="server">The server.</param>
    /// <param name="password">The password that worked, or <c>null</c> when the saved one was used.</param>
    public void MarkConnected(ServerProfile server, string? password)
    {
        if (!string.IsNullOrEmpty(password))
        {
            _passwords[server.Id] = password;
        }

        _connected[server.Id] = true;
    }

    /// <summary>Forgets a session password and marks the server disconnected.</summary>
    /// <param name="id">The server id.</param>
    public void Disconnect(string id)
    {
        _passwords.TryRemove(id, out _);
        _connected.TryRemove(id, out _);
    }

    /// <summary>Gets whether the server was connected in this session.</summary>
    /// <param name="id">The server id.</param>
    /// <returns><c>true</c> if connected.</returns>
    public bool IsConnected(string id) => _connected.ContainsKey(id);

    /// <summary>Resolves a request target to a provider and connection string.</summary>
    /// <param name="target">The request target.</param>
    /// <returns>The provider and connection string.</returns>
    /// <exception cref="IntrospectionException">The target is incomplete or names an unknown server.</exception>
    public (DatabaseProvider Provider, string ConnectionString) Resolve(TargetRequest target)
    {
        if (!string.IsNullOrEmpty(target.ServerId))
        {
            var server = Store.Get(target.ServerId)
                ?? throw new IntrospectionException($"No registered server has id '{target.ServerId}'.");
            return (server.Provider, ConnectionStringFactory.Build(server, Blank(target.Database), PasswordFor(server)));
        }

        if (!Enum.TryParse<DatabaseProvider>(target.Provider, ignoreCase: true, out var provider))
        {
            throw new IntrospectionException($"Unknown provider '{target.Provider}'.");
        }

        if (string.IsNullOrWhiteSpace(target.ConnectionString))
        {
            throw new IntrospectionException("A connection string or a registered server is required.");
        }

        return (provider, target.ConnectionString);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
