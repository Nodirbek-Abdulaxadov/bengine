namespace Bengine.Introspection.Connection;

using Bengine.Enums;
using Bengine.Introspection.Servers;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

/// <summary>
/// Builds a driver connection string from a <see cref="ServerProfile"/> for a
/// particular database on that server, using each driver's own connection-string
/// builder so values are escaped correctly. The profile's free-form
/// <see cref="ServerProfile.Options"/> are applied first and the structured
/// fields override them.
/// </summary>
public static class ConnectionStringFactory
{
    /// <summary>Builds the connection string for one database on the server.</summary>
    /// <param name="server">The server registration.</param>
    /// <param name="database">The database to connect to, or <c>null</c> for the maintenance database.</param>
    /// <param name="password">A password to use instead of the saved one (for example one typed at connect time).</param>
    /// <returns>The driver connection string.</returns>
    /// <exception cref="IntrospectionException">The profile's options are not a valid connection string.</exception>
    public static string Build(ServerProfile server, string? database = null, string? password = null)
    {
        var secret = password ?? server.Password;
        try
        {
            return server.Provider switch
            {
                DatabaseProvider.PostgreSql => BuildPostgreSql(server, database, secret),
                DatabaseProvider.SqlServer => BuildSqlServer(server, database, secret),
                DatabaseProvider.MySql => BuildMySql(server, database, secret),
                DatabaseProvider.Sqlite => BuildSqlite(server),
                _ => throw new ArgumentOutOfRangeException(nameof(server), server.Provider, "Unknown database provider."),
            };
        }
        catch (ArgumentException ex)
        {
            throw new IntrospectionException($"The connection options for '{server.Name}' are invalid. {ex.Message}", ex);
        }
    }

    private static string BuildPostgreSql(ServerProfile server, string? database, string? password)
    {
        var builder = new NpgsqlConnectionStringBuilder(server.Options ?? string.Empty)
        {
            Host = server.Host ?? "localhost",
            Port = server.Port ?? 5432,
            Database = database ?? server.MaintenanceDatabase ?? "postgres",
            Username = server.Username,
            ApplicationName = "Bengine",
        };
        if (!string.IsNullOrEmpty(password))
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    private static string BuildSqlServer(ServerProfile server, string? database, string? password)
    {
        var builder = new SqlConnectionStringBuilder(server.Options ?? string.Empty)
        {
            DataSource = server.Port is { } port ? $"{server.Host ?? "localhost"},{port}" : server.Host ?? "localhost",
            InitialCatalog = database ?? server.MaintenanceDatabase ?? "master",
            ApplicationName = "Bengine",
        };
        if (!string.IsNullOrEmpty(server.Username))
        {
            builder.UserID = server.Username;
        }

        if (!string.IsNullOrEmpty(password))
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    private static string BuildMySql(ServerProfile server, string? database, string? password)
    {
        var builder = new MySqlConnectionStringBuilder(server.Options ?? string.Empty)
        {
            Server = server.Host ?? "localhost",
            Port = (uint)(server.Port ?? 3306),
            UserID = server.Username ?? string.Empty,
        };

        var target = database ?? server.MaintenanceDatabase;
        if (!string.IsNullOrEmpty(target))
        {
            builder.Database = target;
        }

        if (!string.IsNullOrEmpty(password))
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    // ReadWrite (not ReadWriteCreate): a mistyped path must fail rather than
    // silently create an empty database file.
    private static string BuildSqlite(ServerProfile server) =>
        new SqliteConnectionStringBuilder(server.Options ?? string.Empty)
        {
            DataSource = server.FilePath ?? throw new IntrospectionException($"'{server.Name}' has no database file path."),
            Mode = SqliteOpenMode.ReadWrite,
        }.ConnectionString;
}
