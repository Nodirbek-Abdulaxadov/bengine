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

    /// <summary>Creates a store over the given file, or the default location when omitted.</summary>
    /// <param name="path">The JSON file to use; defaults to <see cref="DefaultPath"/>.</param>
    public ServerStore(string? path = null) => FilePath = path ?? DefaultPath;

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
            var stored = profile with { Password = password };

            if (index >= 0)
            {
                all[index] = stored;
            }
            else
            {
                all.Add(stored);
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
            return JsonSerializer.Deserialize<List<ServerProfile>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new IntrospectionException($"The server list at {FilePath} is not valid JSON. {ex.Message}", ex);
        }
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
