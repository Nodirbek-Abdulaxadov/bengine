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

    /// <summary>
    /// Gets a value indicating whether the saved password lives in the operating
    /// system's keyring rather than in the server list file.
    /// </summary>
    public bool PasswordInKeyring { get; init; }

    /// <summary>Gets the SSH tunnel's host, or <c>null</c> to connect directly.</summary>
    public string? SshHost { get; init; }

    /// <summary>Gets the SSH port, or <c>null</c> for 22.</summary>
    public int? SshPort { get; init; }

    /// <summary>Gets the SSH user.</summary>
    public string? SshUser { get; init; }

    /// <summary>Gets the private key file for SSH, or <c>null</c> to authenticate with <see cref="SshPassword"/>.</summary>
    public string? SshKeyFile { get; init; }

    /// <summary>Gets the SSH password, or the key file's passphrase (saved like <see cref="Password"/>).</summary>
    public string? SshPassword { get; init; }

    /// <summary>Gets the SSL mode: <c>disable</c>, <c>prefer</c>, <c>require</c>, <c>verify-ca</c>, or <c>verify-full</c>; <c>null</c> for the driver default.</summary>
    public string? SslMode { get; init; }

    /// <summary>Gets the certificate authority file used to verify the server, or <c>null</c>.</summary>
    public string? SslRootCertificate { get; init; }

    /// <summary>Gets the client certificate file, or <c>null</c>.</summary>
    public string? SslClientCertificate { get; init; }

    /// <summary>Gets the client key file, or <c>null</c>.</summary>
    public string? SslClientKey { get; init; }

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

    /// <summary>
    /// Gets the color that marks the server's tree, tabs, and editors (a
    /// <c>#rrggbb</c> value), so a production server is hard to mistake, or
    /// <c>null</c> for none.
    /// </summary>
    public string? Color { get; init; }

    /// <summary>
    /// Gets a value indicating whether sessions on the server are read-only: the
    /// server refuses writes where it supports read-only sessions, and the
    /// clients refuse modifying statements and data edits.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>Gets a short description of the endpoint, for display (<c>host:port</c> or the file path).</summary>
    public string Endpoint => Provider == DatabaseProvider.Sqlite
        ? FilePath ?? string.Empty
        : (Port is { } port ? $"{Host}:{port}" : Host ?? string.Empty) + (SshHost is { } ssh ? $" via ssh {ssh}" : string.Empty);
}
