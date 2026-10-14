namespace Bengine.Introspection.Projects;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bengine.Introspection.Servers;

/// <summary>Which server and database a SQL file runs against.</summary>
/// <param name="ServerId">The server registration id.</param>
/// <param name="Database">The database, or <c>null</c> for the server's default.</param>
public sealed record FileTarget(string ServerId, string? Database);

/// <summary>One saved version of a file.</summary>
/// <param name="Time">When it was saved.</param>
/// <param name="Path">Where the snapshot is stored.</param>
/// <param name="Length">Its size in characters.</param>
public sealed record HistoryVersion(DateTime Time, string Path, long Length);

/// <summary>
/// The SQL files of a workspace and what they need: the data source each file
/// is attached to, scratch files, and a local history of every saved version —
/// all kept beside the server list (<c>~/.config/bengine</c>), never inside the
/// user's project folder.
/// </summary>
public sealed class SqlFiles
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();

    /// <summary>Creates the store under a directory.</summary>
    /// <param name="directory">The configuration directory, or <c>null</c> for the one beside the server list.</param>
    public SqlFiles(string? directory = null) =>
        Directory = directory ?? System.IO.Path.GetDirectoryName(ServerStore.DefaultPath)!;

    /// <summary>Gets the configuration directory.</summary>
    public string Directory { get; }

    /// <summary>Gets the scratch file directory.</summary>
    public string ScratchDirectory => System.IO.Path.Combine(Directory, "scratches");

    private string AssociationsPath => System.IO.Path.Combine(Directory, "files.json");

    private string HistoryRoot => System.IO.Path.Combine(Directory, "local-history");

    /// <summary>Gets the data source a file is attached to.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The target, or <c>null</c>.</returns>
    public FileTarget? TargetOf(string path)
    {
        lock (_gate)
        {
            return LoadAssociations().GetValueOrDefault(Normalize(path));
        }
    }

    /// <summary>Attaches a file to a data source.</summary>
    /// <param name="path">The file.</param>
    /// <param name="target">The data source.</param>
    public void Attach(string path, FileTarget target)
    {
        lock (_gate)
        {
            var all = LoadAssociations();
            all[Normalize(path)] = target;
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(AssociationsPath, JsonSerializer.Serialize(all, Json));
        }
    }

    /// <summary>Creates a new, empty scratch file.</summary>
    /// <param name="extension">The extension without a dot.</param>
    /// <returns>Its path.</returns>
    public string NewScratch(string extension = "sql")
    {
        System.IO.Directory.CreateDirectory(ScratchDirectory);
        for (var n = 1; ; n++)
        {
            var path = System.IO.Path.Combine(ScratchDirectory, $"scratch_{n}.{extension}");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, string.Empty);
                return path;
            }
        }
    }

    /// <summary>Saves a file and records the new content in its local history (unless it did not change).</summary>
    /// <param name="path">The file.</param>
    /// <param name="content">The content.</param>
    public void Save(string path, string content)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        File.WriteAllText(path, content);
        Snapshot(path, content);
    }

    /// <summary>Records a version of a file in its local history.</summary>
    /// <param name="path">The file.</param>
    /// <param name="content">The content.</param>
    public void Snapshot(string path, string content)
    {
        lock (_gate)
        {
            var folder = HistoryFolder(path);
            System.IO.Directory.CreateDirectory(folder);
            var latest = System.IO.Directory.GetFiles(folder, "*.sql").Order(StringComparer.Ordinal).LastOrDefault();
            if (latest is not null && File.ReadAllText(latest) == content)
            {
                return;
            }

            // Saves within the same millisecond each keep their version.
            var stamp = DateTime.Now;
            while (File.Exists(System.IO.Path.Combine(folder, $"{stamp:yyyyMMdd-HHmmss-fff}.sql")))
            {
                stamp = stamp.AddMilliseconds(1);
            }

            File.WriteAllText(System.IO.Path.Combine(folder, $"{stamp:yyyyMMdd-HHmmss-fff}.sql"), content);
            File.WriteAllText(System.IO.Path.Combine(folder, "source.txt"), System.IO.Path.GetFullPath(path));

            // Keep the newest 200 versions.
            foreach (var old in System.IO.Directory.GetFiles(folder, "*.sql").Order(StringComparer.Ordinal).SkipLast(200))
            {
                File.Delete(old);
            }
        }
    }

    /// <summary>Lists a file's saved versions, newest first.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The versions.</returns>
    public IReadOnlyList<HistoryVersion> History(string path)
    {
        var folder = HistoryFolder(path);
        if (!System.IO.Directory.Exists(folder))
        {
            return [];
        }

        return System.IO.Directory.GetFiles(folder, "*.sql")
            .Select(f => (File: f, Stamp: System.IO.Path.GetFileNameWithoutExtension(f)))
            .Select(f => new HistoryVersion(
                DateTime.TryParseExact(f.Stamp, "yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time) ? time : File.GetLastWriteTime(f.File),
                f.File,
                new FileInfo(f.File).Length))
            .OrderByDescending(v => v.Time)
            .ToList();
    }

    private string HistoryFolder(string path) =>
        System.IO.Path.Combine(HistoryRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(path))))[..20]);

    private Dictionary<string, FileTarget> LoadAssociations()
    {
        try
        {
            return File.Exists(AssociationsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, FileTarget>>(File.ReadAllText(AssociationsPath), Json) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static string Normalize(string path) => System.IO.Path.GetFullPath(path);
}
