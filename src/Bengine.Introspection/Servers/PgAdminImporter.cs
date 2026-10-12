namespace Bengine.Introspection.Servers;

using System.Text.Json;
using Bengine.Enums;
using Microsoft.Data.Sqlite;

/// <summary>
/// Reads the server registrations of a local pgAdmin 4 desktop installation from
/// its SQLite configuration database, so they can be imported into Bengine. Only
/// the endpoint, login name, group, and SSL settings are imported: pgAdmin
/// encrypts saved passwords with its own master key, so imported servers start
/// without a password and prompt for one on first connect. The pgAdmin file is
/// opened read-only and never modified.
/// </summary>
public static class PgAdminImporter
{
    /// <summary>Gets the candidate locations of pgAdmin's configuration database, most likely first.</summary>
    public static IReadOnlyList<string> CandidatePaths
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return
            [
                Path.Combine(home, ".pgadmin", "pgadmin4.db"),
                Path.Combine(appData, "pgAdmin", "pgadmin4.db"),
            ];
        }
    }

    /// <summary>Gets the first candidate path that exists, or <c>null</c> if pgAdmin is not installed.</summary>
    public static string? FindDatabase() => CandidatePaths.FirstOrDefault(File.Exists);

    /// <summary>Reads pgAdmin's server registrations.</summary>
    /// <param name="path">The pgAdmin configuration database; defaults to <see cref="FindDatabase"/>.</param>
    /// <returns>The registrations as PostgreSQL <see cref="ServerProfile"/>s without passwords.</returns>
    /// <exception cref="IntrospectionException">No pgAdmin database was found or it could not be read.</exception>
    public static IReadOnlyList<ServerProfile> Read(string? path = null)
    {
        var file = path ?? FindDatabase()
            ?? throw new IntrospectionException("No pgAdmin 4 configuration database was found.");

        var builder = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly };
        try
        {
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.name, COALESCE(g.name, 'Servers') AS grp, s.host, s.port,
                       s.maintenance_db, s.username, s.comment, s.connection_params
                FROM server s
                LEFT JOIN servergroup g ON g.id = s.servergroup_id
                ORDER BY s.id
                """;

            using var reader = command.ExecuteReader();
            var servers = new List<ServerProfile>();
            while (reader.Read())
            {
                servers.Add(new ServerProfile
                {
                    Name = reader.GetString(0),
                    Group = reader.GetString(1),
                    Provider = DatabaseProvider.PostgreSql,
                    Host = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Port = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    MaintenanceDatabase = reader.IsDBNull(4) ? "postgres" : reader.GetString(4),
                    Username = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Comment = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Options = reader.IsDBNull(7) ? null : ToNpgsqlOptions(reader.GetString(7)),
                });
            }

            return servers;
        }
        catch (SqliteException ex)
        {
            throw new IntrospectionException($"Could not read the pgAdmin configuration at {file}. {ex.Message}", ex);
        }
    }

    // pgAdmin stores libpq connection parameters as JSON; carry over the ones
    // that have a direct Npgsql equivalent.
    private static string? ToNpgsqlOptions(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var parts = new List<string>();
            if (document.RootElement.TryGetProperty("sslmode", out var ssl) && ssl.ValueKind == JsonValueKind.String)
            {
                var mode = ssl.GetString() switch
                {
                    "disable" => "Disable",
                    "allow" => "Allow",
                    "prefer" => "Prefer",
                    "require" => "Require",
                    "verify-ca" => "VerifyCA",
                    "verify-full" => "VerifyFull",
                    _ => null,
                };
                if (mode is not null)
                {
                    parts.Add($"SSL Mode={mode}");
                }
            }

            if (document.RootElement.TryGetProperty("connect_timeout", out var timeout)
                && timeout.ValueKind == JsonValueKind.Number && timeout.GetInt32() > 0)
            {
                parts.Add($"Timeout={timeout.GetInt32()}");
            }

            return parts.Count == 0 ? null : string.Join(";", parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
