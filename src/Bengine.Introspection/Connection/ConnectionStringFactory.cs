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
                DatabaseProvider.Oracle => BuildOracle(server, database, secret),
                DatabaseProvider.MongoDb => BuildMongo(server, database, secret),
                _ => throw new ArgumentOutOfRangeException(nameof(server), server.Provider, "Unknown database provider."),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or MongoDB.Driver.MongoConfigurationException)
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

        if (Ssl(server.SslMode) is { } mode)
        {
            builder.SslMode = mode switch
            {
                "disable" => SslMode.Disable,
                "require" => SslMode.Require,
                "verify-ca" => SslMode.VerifyCA,
                "verify-full" => SslMode.VerifyFull,
                _ => SslMode.Prefer,
            };
        }

        if (!string.IsNullOrWhiteSpace(server.SslRootCertificate))
        {
            builder.RootCertificate = server.SslRootCertificate;
        }

        if (!string.IsNullOrWhiteSpace(server.SslClientCertificate))
        {
            builder.SslCertificate = server.SslClientCertificate;
        }

        if (!string.IsNullOrWhiteSpace(server.SslClientKey))
        {
            builder.SslKey = server.SslClientKey;
        }

        return builder.ConnectionString;
    }

    // Normalized SSL mode, or null for the driver default.
    private static string? Ssl(string? mode) => string.IsNullOrWhiteSpace(mode) ? null : mode.Trim().ToLowerInvariant();

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

        switch (Ssl(server.SslMode))
        {
            case "disable" or "prefer":
                builder.Encrypt = SqlConnectionEncryptOption.Optional;
                break;
            case "require":
                builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
                builder.TrustServerCertificate = true;
                break;
            case "verify-ca" or "verify-full":
                builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
                builder.TrustServerCertificate = false;
                break;
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

        if (Ssl(server.SslMode) is { } mode)
        {
            builder.SslMode = mode switch
            {
                "disable" => MySqlSslMode.None,
                "require" => MySqlSslMode.Required,
                "verify-ca" => MySqlSslMode.VerifyCA,
                "verify-full" => MySqlSslMode.VerifyFull,
                _ => MySqlSslMode.Preferred,
            };
        }

        if (!string.IsNullOrWhiteSpace(server.SslRootCertificate))
        {
            builder.SslCa = server.SslRootCertificate;
        }

        if (!string.IsNullOrWhiteSpace(server.SslClientCertificate))
        {
            builder.SslCert = server.SslClientCertificate;
        }

        if (!string.IsNullOrWhiteSpace(server.SslClientKey))
        {
            builder.SslKey = server.SslClientKey;
        }

        return builder.ConnectionString;
    }

    // Oracle: a "database" is a service (a pluggable database); TCPS when SSL is asked.
    private static string BuildOracle(ServerProfile server, string? database, string? password)
    {
        var service = database ?? server.MaintenanceDatabase
            ?? throw new IntrospectionException($"'{server.Name}' has no Oracle service name (server properties → Service).");
        var host = server.Host ?? "localhost";
        var port = server.Port ?? 1521;
        var mode = Ssl(server.SslMode);
        var builder = new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(server.Options ?? string.Empty)
        {
            DataSource = mode is "require" or "verify-ca" or "verify-full"
                ? $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST={host})(PORT={port}))(CONNECT_DATA=(SERVICE_NAME={service}))" +
                  $"(SECURITY=(SSL_SERVER_DN_MATCH={(mode == "verify-full" ? "YES" : "NO")})))"
                : $"{host}:{port}/{service}",
            UserID = server.Username ?? string.Empty,
        };
        if (!string.IsNullOrEmpty(password))
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    // MongoDB: a URL. The host may itself be a full mongodb:// or mongodb+srv://
    // URL (Atlas); options are URL query parameters (replicaSet=rs0&…); the
    // maintenance database is the authentication database.
    private static string BuildMongo(ServerProfile server, string? database, string? password)
    {
        var host = server.Host?.Trim() ?? "localhost";
        var isUrl = host.StartsWith("mongodb://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("mongodb+srv://", StringComparison.OrdinalIgnoreCase);
        var url = isUrl ? host : $"mongodb://{(host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host)}:{server.Port ?? 27017}/";
        if (!string.IsNullOrWhiteSpace(server.Options))
        {
            var options = string.Join('&', server.Options.Split(['\n', '\r', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(o => o.TrimStart('?')));
            url += (url.Contains('?') ? "&" : url.EndsWith('/') ? "?" : "/?") + options;
        }

        var builder = new MongoDB.Driver.MongoUrlBuilder(url);
        if (!isUrl && builder.Servers.Count() == 1 && builder.ReplicaSetName is null)
        {
            // One host (possibly through an SSH tunnel) is talked to directly, not discovered as a replica set.
            builder.DirectConnection = true;
        }

        if (!string.IsNullOrEmpty(server.Username))
        {
            builder.Username = server.Username;
            builder.Password = password;
            builder.AuthenticationSource ??= server.MaintenanceDatabase ?? "admin";
        }

        builder.DatabaseName = database ?? server.MaintenanceDatabase ?? builder.DatabaseName ?? "admin";
        switch (Ssl(server.SslMode))
        {
            case "disable":
                builder.UseTls = false;
                break;
            case "prefer" or "require":
                builder.UseTls = true;
                builder.AllowInsecureTls = true;
                break;
            case "verify-ca" or "verify-full":
                builder.UseTls = true;
                break;
        }

        return builder.ToString();
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
