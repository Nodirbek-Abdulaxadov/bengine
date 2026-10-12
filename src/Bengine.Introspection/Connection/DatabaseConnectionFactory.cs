namespace Bengine.Introspection.Connection;

using System.Data.Common;
using Bengine.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

/// <summary>
/// Creates the concrete ADO.NET <see cref="DbConnection"/> for a
/// <see cref="DatabaseProvider"/> from a connection string. This is the single
/// place the project names a driver type; everything above it works against the
/// provider-independent <see cref="DbConnection"/> base class.
/// </summary>
public static class DatabaseConnectionFactory
{
    /// <summary>Creates a closed connection for the provider.</summary>
    /// <param name="provider">The target provider.</param>
    /// <param name="connectionString">The driver-specific connection string.</param>
    /// <returns>A new, unopened connection.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The provider is not recognized.</exception>
    public static DbConnection Create(DatabaseProvider provider, string connectionString) => provider switch
    {
        DatabaseProvider.PostgreSql => new NpgsqlConnection(connectionString),
        DatabaseProvider.SqlServer => new SqlConnection(connectionString),
        DatabaseProvider.MySql => new MySqlConnection(connectionString),
        DatabaseProvider.Sqlite => new SqliteConnection(connectionString),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider."),
    };

    /// <summary>Creates and opens a connection for the provider.</summary>
    /// <param name="provider">The target provider.</param>
    /// <param name="connectionString">The driver-specific connection string.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>An opened connection.</returns>
    /// <exception cref="Introspection.IntrospectionException">The connection could not be opened.</exception>
    public static async Task<DbConnection> OpenAsync(
        DatabaseProvider provider,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        DbConnection? connection = null;
        try
        {
            // Create is inside the try: an invalid connection string (for example a
            // PostgreSQL string handed to the SQLite driver) throws here, and must
            // surface as an IntrospectionException, not an unhandled 500.
            connection = Create(provider, connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            throw new IntrospectionException(
                $"Could not open a {provider} connection. {ex.Message}", ex);
        }
    }
}
