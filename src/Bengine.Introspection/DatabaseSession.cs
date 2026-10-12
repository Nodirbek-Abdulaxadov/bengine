namespace Bengine.Introspection;

using System.Data.Common;
using Bengine.Enums;
using Bengine.Introspection.Abstractions;
using Bengine.Introspection.Connection;
using Bengine.Introspection.Data;
using Bengine.Introspection.Providers;

/// <summary>
/// An open, provider-independent working session against one live database. It
/// owns the connection and exposes the three capabilities a database browser
/// needs over it — discovery (<see cref="Explorer"/>), schema reconstruction
/// (<see cref="Introspector"/>), and paged data reads (<see cref="Data"/>) —
/// each already wired to the correct provider implementation. Open it with
/// <see cref="OpenAsync"/> and dispose it to close the connection.
/// </summary>
public sealed class DatabaseSession : IAsyncDisposable
{
    private readonly DbConnection _connection;

    private DatabaseSession(
        DbConnection connection,
        DatabaseProvider provider,
        SchemaProviderBase schemaProvider)
    {
        _connection = connection;
        Provider = provider;
        Explorer = schemaProvider;
        Introspector = schemaProvider;
        Data = new RelationalDataReader(connection, provider);
        Writer = new RelationalDataWriter(connection, provider);
        Query = new RelationalQueryExecutor(connection, provider);
    }

    /// <summary>Gets the provider this session speaks to.</summary>
    public DatabaseProvider Provider { get; }

    /// <summary>Gets the discovery capability: databases, schemas, and relations.</summary>
    public IDatabaseExplorer Explorer { get; }

    /// <summary>Gets the introspection capability: reconstruct the schema AST.</summary>
    public IDatabaseIntrospector Introspector { get; }

    /// <summary>Gets the data-browsing capability: paged, filtered, sorted reads.</summary>
    public ITableDataReader Data { get; }

    /// <summary>Gets the data-editing capability: insert, update, and delete rows.</summary>
    public ITableDataWriter Writer { get; }

    /// <summary>Gets the query-console capability: run ad-hoc SQL.</summary>
    public IQueryExecutor Query { get; }

    /// <summary>Opens a session against a live database.</summary>
    /// <param name="provider">The target provider.</param>
    /// <param name="connectionString">The driver-specific connection string.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>An open session.</returns>
    /// <exception cref="IntrospectionException">The connection could not be opened.</exception>
    public static async Task<DatabaseSession> OpenAsync(
        DatabaseProvider provider,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var connection = await DatabaseConnectionFactory
            .OpenAsync(provider, connectionString, cancellationToken)
            .ConfigureAwait(false);

        var schemaProvider = CreateProvider(provider, connection);
        return new DatabaseSession(connection, provider, schemaProvider);
    }

    /// <summary>Verifies that a connection string can connect, without keeping the session.</summary>
    /// <param name="provider">The target provider.</param>
    /// <param name="connectionString">The connection string to test.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns><c>true</c> if a connection opened; otherwise <c>false</c>.</returns>
    public static async Task<bool> CanConnectAsync(
        DatabaseProvider provider,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await OpenAsync(provider, connectionString, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (IntrospectionException)
        {
            return false;
        }
    }

    private static SchemaProviderBase CreateProvider(DatabaseProvider provider, DbConnection connection) => provider switch
    {
        DatabaseProvider.PostgreSql => new PostgreSqlSchemaProvider(connection),
        DatabaseProvider.SqlServer => new SqlServerSchemaProvider(connection),
        DatabaseProvider.MySql => new MySqlSchemaProvider(connection),
        DatabaseProvider.Sqlite => new SqliteSchemaProvider(connection),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider."),
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);
}
