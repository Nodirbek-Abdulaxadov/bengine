namespace Bengine.Introspection.Servers;

using Bengine.Enums;

/// <summary>
/// A saved server registration, the equivalent of a pgAdmin "Register Server"
/// entry: where the server is, how to log in, and which database to use for
/// server-level work. A profile describes a <em>server</em>, not one database —
/// the object explorer derives a per-database connection from it as the user
/// expands the tree.
/// </summary>
public sealed record ServerProfile
{
    /// <summary>Gets the stable identifier of the registration.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets the display name shown in the tree.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the server group the registration is listed under.</summary>
    public string Group { get; init; } = "Servers";

    /// <summary>Gets the database provider.</summary>
    public required DatabaseProvider Provider { get; init; }

    /// <summary>Gets the host name or address; unused for SQLite.</summary>
    public string? Host { get; init; }

    /// <summary>Gets the TCP port, or <c>null</c> for the provider default; unused for SQLite.</summary>
    public int? Port { get; init; }

    /// <summary>
    /// Gets the database to connect to for server-level work (listing databases,
    /// roles, and tablespaces) — pgAdmin's "maintenance database".
    /// </summary>
    public string? MaintenanceDatabase { get; init; }

    /// <summary>Gets the login name; unused for SQLite.</summary>
    public string? Username { get; init; }

    /// <summary>Gets the saved password, present only when <see cref="SavePassword"/> is set.</summary>
    public string? Password { get; init; }

    /// <summary>Gets a value indicating whether the password is persisted with the registration.</summary>
    public bool SavePassword { get; init; }

    /// <summary>Gets the database file path; used only by SQLite.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets additional driver parameters in connection-string form (for example
    /// <c>SSL Mode=Require</c> or <c>TrustServerCertificate=True</c>), applied
    /// before the structured fields.
    /// </summary>
    public string? Options { get; init; }

    /// <summary>Gets an optional free-text comment.</summary>
    public string? Comment { get; init; }

    /// <summary>Gets a short description of the endpoint, for display (<c>host:port</c> or the file path).</summary>
    public string Endpoint => Provider == DatabaseProvider.Sqlite
        ? FilePath ?? string.Empty
        : Port is { } port ? $"{Host}:{port}" : Host ?? string.Empty;
}
