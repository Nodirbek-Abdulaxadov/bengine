namespace Bengine.Introspection.Tree;

using System.Data.Common;
using System.Text.RegularExpressions;
using Bengine.Enums;
using Bengine.Introspection.Connection;
using Bengine.Introspection.Servers;

/// <summary>
/// The entry point of the object explorer. Given a registered server and a node,
/// it opens a connection to the database the node lives in (the maintenance
/// database for server-level nodes), runs the provider's tree over it, and
/// closes it again — driver connection pooling makes the per-call connection
/// cheap. It also opens full <see cref="DatabaseSession"/>s for the data and
/// query views.
/// </summary>
public static partial class ObjectExplorer
{
    /// <summary>Lists the children of a node.</summary>
    /// <param name="server">The registered server.</param>
    /// <param name="password">A password to use instead of the saved one, or <c>null</c>.</param>
    /// <param name="node">The node to expand.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The children.</returns>
    /// <exception cref="IntrospectionException">The connection or a catalog query failed.</exception>
    public static async Task<IReadOnlyList<TreeNode>> GetChildrenAsync(
        ServerProfile server,
        string? password,
        NodeRef node,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(server, password, node.Database, cancellationToken).ConfigureAwait(false);
        return await Create(server.Provider, connection).GetChildrenAsync(node, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the properties of a node. An object node returns its properties and
    /// DDL; a folder returns a grid listing its children, as pgAdmin's Properties
    /// tab does for a collection.
    /// </summary>
    /// <param name="server">The registered server.</param>
    /// <param name="password">A password to use instead of the saved one, or <c>null</c>.</param>
    /// <param name="node">The node.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The node's properties.</returns>
    /// <exception cref="IntrospectionException">The connection or a catalog query failed.</exception>
    public static async Task<ObjectProperties> GetPropertiesAsync(
        ServerProfile server,
        string? password,
        NodeRef node,
        CancellationToken cancellationToken = default)
    {
        if (node.IsFolder)
        {
            var children = await GetChildrenAsync(server, password, node, cancellationToken).ConfigureAwait(false);
            return new ObjectProperties
            {
                Title = FolderTitle(node.Kind),
                GridColumns = ["Name", "Details"],
                GridRows = children
                    .Select(c => (IReadOnlyList<string?>)[c.Label, c.Detail ?? c.Badge ?? c.Count?.ToString(System.Globalization.CultureInfo.InvariantCulture)])
                    .ToList(),
            };
        }

        // Database properties come from server-wide catalogs; read them through the
        // maintenance database so a database that refuses connections still shows.
        var database = node.Kind == NodeKind.Database ? null : node.Database;
        await using var connection = await OpenAsync(server, password, database, cancellationToken).ConfigureAwait(false);
        var properties = await Create(server.Provider, connection).GetPropertiesAsync(node, cancellationToken).ConfigureAwait(false);

        if (node.Kind == NodeKind.Server)
        {
            var registration = new List<PropertyItem>
            {
                new("Name", server.Name),
                new("Provider", server.Provider.ToString()),
                new("Endpoint", server.Endpoint),
            };
            if (!string.IsNullOrEmpty(server.Username))
            {
                registration.Add(new("Username", server.Username));
            }

            return properties with { Title = server.Name, Properties = [.. registration, .. properties.Properties] };
        }

        return properties;
    }

    /// <summary>Opens a working session (data, edits, query console) on one database of the server.</summary>
    /// <param name="server">The registered server.</param>
    /// <param name="password">A password to use instead of the saved one, or <c>null</c>.</param>
    /// <param name="database">The database, or <c>null</c> for the maintenance database.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>An open session.</returns>
    /// <exception cref="IntrospectionException">The connection could not be opened.</exception>
    public static async Task<DatabaseSession> OpenSessionAsync(
        ServerProfile server,
        string? password,
        string? database,
        CancellationToken cancellationToken = default)
    {
        var routed = await SshTunnels.RouteAsync(server, cancellationToken).ConfigureAwait(false);
        return await DatabaseSession.OpenAsync(
            server.Provider,
            ConnectionStringFactory.Build(routed, database, password),
            new SessionOptions { ReadOnly = server.ReadOnly },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Verifies that the server accepts a connection with the given credentials.</summary>
    /// <param name="server">The registered server.</param>
    /// <param name="password">A password to use instead of the saved one, or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>A task that completes when the connection was opened and closed.</returns>
    /// <exception cref="IntrospectionException">The connection could not be opened.</exception>
    public static async Task TestAsync(ServerProfile server, string? password, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(server, password, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets whether a failure was caused by rejected or missing credentials, so a
    /// client can prompt for a password instead of showing a generic error.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns><c>true</c> if the server refused the login.</returns>
    public static bool IsAuthenticationError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case Npgsql.PostgresException pg when pg.SqlState is "28P01" or "28000":
                    return true;
                case Microsoft.Data.SqlClient.SqlException sql when sql.Number == 18456:
                    return true;
                case MySqlConnector.MySqlException my when my.ErrorCode == MySqlConnector.MySqlErrorCode.AccessDenied:
                    return true;
                case Npgsql.NpgsqlException npgsql when npgsql.Message.Contains("password", StringComparison.OrdinalIgnoreCase):
                    return true;
            }
        }

        return false;
    }

    /// <summary>Creates the provider's tree over an open connection.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="connection">An open connection.</param>
    /// <returns>The tree.</returns>
    public static IObjectTree Create(DatabaseProvider provider, DbConnection connection) => provider switch
    {
        DatabaseProvider.PostgreSql => new PostgreSqlObjectTree(connection),
        DatabaseProvider.SqlServer => new SqlServerObjectTree(connection),
        DatabaseProvider.MySql => new MySqlObjectTree(connection),
        DatabaseProvider.Sqlite => new SqliteObjectTree(connection),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider."),
    };

    /// <summary>Turns a folder kind into its display title (<c>MaterializedViewsFolder</c> → "Materialized Views").</summary>
    /// <param name="kind">The folder kind.</param>
    /// <returns>The title.</returns>
    public static string FolderTitle(NodeKind kind) => kind switch
    {
        NodeKind.RolesFolder => "Login/Group Roles",
        _ => SplitWords().Replace(kind.ToString().Replace("Folder", string.Empty, StringComparison.Ordinal), " $1").Trim(),
    };

    private static async Task<DbConnection> OpenAsync(ServerProfile server, string? password, string? database, CancellationToken cancellationToken)
    {
        var routed = await SshTunnels.RouteAsync(server, cancellationToken).ConfigureAwait(false);
        return await DatabaseConnectionFactory.OpenAsync(server.Provider, ConnectionStringFactory.Build(routed, database, password), cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex SplitWords();
}
