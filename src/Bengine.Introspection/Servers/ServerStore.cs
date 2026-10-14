namespace Bengine.Introspection.Servers;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Persists server registrations as JSON in the user's configuration directory
/// (<c>~/.config/bengine/servers.json</c> on Linux, <c>%APPDATA%\bengine</c> on
/// Windows), so the web explorer and the desktop Studio share one server list.
/// The file can hold saved passwords, so on Unix it is written with owner-only
/// (<c>600</c>) permissions. Writes are atomic: a temporary file is written and
/// then moved over the original.
/// </summary>
public sealed class ServerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _secrets = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a store over the given file, or the default location when
    /// omitted. The default store keeps saved passwords in the operating
    /// system's keyring when one is available; a store over an explicit file
    /// uses only the <paramref name="credentials"/> it is given.
    /// </summary>
    /// <param name="path">The JSON file to use; defaults to <see cref="DefaultPath"/>.</param>
    /// <param name="credentials">The keyring, or <c>null</c> to keep passwords in the file.</param>
    public ServerStore(string? path = null, ICredentialStore? credentials = null)
    {
        FilePath = path ?? DefaultPath;
        Credentials = path is null ? credentials ?? CredentialStores.System : credentials;
    }

    /// <summary>Gets the keyring saved passwords go to, or <c>null</c> when they stay in the file.</summary>
    public ICredentialStore? Credentials { get; }

    /// <summary>
    /// Gets the default store location. The <c>BENGINE_SERVERS_FILE</c>
    /// environment variable overrides it.
    /// </summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("BENGINE_SERVERS_FILE") is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "bengine",
                "servers.json");

    /// <summary>Gets the file this store reads and writes.</summary>
    public string FilePath { get; }

    /// <summary>Lists every registration, ordered by group then name.</summary>
    /// <returns>The registrations.</returns>
    public IReadOnlyList<ServerProfile> List()
    {
        lock (_gate)
        {
            return Load()
                .OrderBy(s => s.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Finds a registration by id.</summary>
    /// <param name="id">The registration id.</param>
    /// <returns>The registration, or <c>null</c> if none has that id.</returns>
    public ServerProfile? Get(string id)
    {
        lock (_gate)
        {
            return Load().FirstOrDefault(s => s.Id == id);
        }
    }

    /// <summary>
    /// Adds or replaces a registration. When the incoming profile asks to save its
    /// password but carries none, the previously saved password is kept, so an
    /// edit form never has to echo the secret back. When it does not ask to save
    /// the password, any saved one is discarded.
    /// </summary>
    /// <param name="profile">The registration to store.</param>
    /// <returns>The stored registration.</returns>
    public ServerProfile Upsert(ServerProfile profile)
    {
        lock (_gate)
        {
            var all = Load();
            var index = all.FindIndex(s => s.Id == profile.Id);
            var existing = index >= 0 ? all[index] : null;

            var password = profile.SavePassword
                ? string.IsNullOrEmpty(profile.Password) ? existing?.Password : profile.Password
                : null;
            var sshPassword = profile.SavePassword
                ? string.IsNullOrEmpty(profile.SshPassword) ? existing?.SshPassword : profile.SshPassword
                : null;
            var stored = profile with { Password = password, SshPassword = sshPassword, PasswordInKeyring = false };

            // Prefer the keyring: the file then records only that a password is saved.
            if (Credentials is { } keyring)
            {
                var inKeyring = password is not null || sshPassword is not null;
                if (inKeyring
                    && Put(keyring, profile.Id, $"Bengine — {profile.Name}", password)
                    && Put(keyring, profile.Id + ":ssh", $"Bengine — {profile.Name} (SSH)", sshPassword))
                {
                    stored = stored with { PasswordInKeyring = true };
                }
                else if (!inKeyring)
                {
                    Forget(keyring, profile.Id);
                }
            }

            var persisted = stored.PasswordInKeyring ? stored with { Password = null, SshPassword = null } : stored;

            if (index >= 0)
            {
                all[index] = persisted;
            }
            else
            {
                all.Add(persisted);
            }

            Save(all);
            return stored;
        }
    }

    /// <summary>Removes a registration.</summary>
    /// <param name="id">The registration id.</param>
    /// <returns><c>true</c> if a registration was removed.</returns>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var all = Load();
            var removed = all.RemoveAll(s => s.Id == id) > 0;
            if (removed)
            {
                Save(all);
                if (Credentials is { } keyring)
                {
                    Forget(keyring, id);
                }
            }

            return removed;
        }
    }

    private List<ServerProfile> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var servers = JsonSerializer.Deserialize<List<ServerProfile>>(json, JsonOptions) ?? [];
            return servers.Select(Unseal).ToList();
        }
        catch (JsonException ex)
        {
            throw new IntrospectionException($"The server list at {FilePath} is not valid JSON. {ex.Message}", ex);
        }
    }

    // Fills in passwords kept in the keyring (cached for the process).
    private ServerProfile Unseal(ServerProfile server)
    {
        if (!server.PasswordInKeyring || Credentials is not { } keyring)
        {
            return server;
        }

        return server with { Password = Cached(keyring, server.Id), SshPassword = Cached(keyring, server.Id + ":ssh") };
    }

    private string? Cached(ICredentialStore keyring, string key)
    {
        if (!_secrets.TryGetValue(key, out var secret))
        {
            try
            {
                secret = keyring.Get(key);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                secret = null;
            }

            _secrets[key] = secret;
        }

        return secret;
    }

    private bool Put(ICredentialStore keyring, string key, string label, string? secret)
    {
        try
        {
            if (secret is null)
            {
                keyring.Delete(key);
                _secrets[key] = null;
                return true;
            }

            var stored = keyring.Set(key, label, secret);
            _secrets[key] = stored ? secret : null;
            return stored;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private void Forget(ICredentialStore keyring, string id)
    {
        Put(keyring, id, string.Empty, null);
        Put(keyring, id + ":ssh", string.Empty, null);
    }

    private void Save(List<ServerProfile> servers)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(servers, JsonOptions));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, FilePath, overwrite: true);
    }
}
