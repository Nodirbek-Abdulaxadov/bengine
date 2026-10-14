namespace Bengine.Studio.ViewModels;

using System.Text.Json;
using Bengine.Introspection.Servers;

/// <summary>
/// The Studio's own preferences, beside the server list
/// (<c>~/.config/bengine/studio.json</c>): the project folder of the Files
/// panel and keyboard shortcut overrides.
/// </summary>
public sealed class StudioSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Gets or sets the folder the Files panel shows.</summary>
    public string ProjectFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BengineProjects");

    /// <summary>Gets or sets the folder of the database client tools (pg_dump, mysqldump), or <c>null</c> to search the PATH.</summary>
    public string? ToolDirectory { get; set; }

    /// <summary>Gets or sets shortcut overrides: action id → gesture (<c>Ctrl+Shift+E</c>).</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets the file the settings are kept in.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath { get; private set; } = string.Empty;

    /// <summary>Loads the settings (defaults when the file is missing or broken).</summary>
    /// <param name="path">The file, or <c>null</c> for the default beside the server list.</param>
    /// <returns>The settings.</returns>
    public static StudioSettings Load(string? path = null)
    {
        path ??= Path.Combine(Path.GetDirectoryName(ServerStore.DefaultPath)!, "studio.json");
        StudioSettings settings;
        try
        {
            settings = File.Exists(path) ? JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            settings = new();
        }

        settings.FilePath = path;
        return settings;
    }

    /// <summary>Saves the settings.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
