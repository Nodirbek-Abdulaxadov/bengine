namespace Bengine.Introspection.History;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>One statement run in a console.</summary>
public sealed record QueryHistoryEntry
{
    /// <summary>Gets when the statement ran.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>Gets the server registration it ran on.</summary>
    public required string ServerId { get; init; }

    /// <summary>Gets the database it ran in.</summary>
    public string? Database { get; init; }

    /// <summary>Gets the statement text.</summary>
    public required string Sql { get; init; }

    /// <summary>Gets how long it took, in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Gets the rows returned or affected, when known.</summary>
    public long? Rows { get; init; }

    /// <summary>Gets the error message, when it failed.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// The query history of the consoles: every executed statement, per server, as
/// JSON lines under <c>~/.config/bengine/history</c> (owner-only on Unix, since
/// statements can contain data). Each file keeps the most recent
/// <see cref="Capacity"/> entries.
/// </summary>
public sealed class QueryHistory
{
    /// <summary>The most entries kept per server.</summary>
    public const int Capacity = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    /// <summary>Creates a history over a directory.</summary>
    /// <param name="directory">The directory, or <c>null</c> for the default next to the server list.</param>
    public QueryHistory(string? directory = null) =>
        Directory = directory ?? Path.Combine(Path.GetDirectoryName(Servers.ServerStore.DefaultPath)!, "history");

    /// <summary>Gets the history directory.</summary>
    public string Directory { get; }

    /// <summary>Appends an entry.</summary>
    /// <param name="entry">The entry.</param>
    public void Add(QueryHistoryEntry entry)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(entry.ServerId);
            var isNew = !File.Exists(path);
            File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + "\n");
            if (isNew && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            // Compact occasionally rather than on every append.
            if (new FileInfo(path).Length > 4_000_000)
            {
                var lines = File.ReadAllLines(path);
                File.WriteAllLines(path, lines.Skip(Math.Max(0, lines.Length - Capacity)));
            }
        }
    }

    /// <summary>Lists entries newest first, optionally filtered by text.</summary>
    /// <param name="serverId">The server, or <c>null</c> for every server.</param>
    /// <param name="search">Text the statement must contain (case-insensitive), or <c>null</c>.</param>
    /// <param name="limit">The most entries to return.</param>
    /// <returns>The entries.</returns>
    public IReadOnlyList<QueryHistoryEntry> List(string? serverId = null, string? search = null, int limit = 500)
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return [];
            }

            var files = serverId is null
                ? System.IO.Directory.GetFiles(Directory, "*.jsonl")
                : File.Exists(PathFor(serverId)) ? [PathFor(serverId)] : [];

            return files
                .SelectMany(File.ReadLines)
                .Select(Parse)
                .OfType<QueryHistoryEntry>()
                .Where(e => string.IsNullOrEmpty(search) || e.Sql.Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Time)
                .Take(limit)
                .ToList();
        }
    }

    private string PathFor(string serverId) =>
        Path.Combine(Directory, string.Concat(serverId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')) + ".jsonl");

    private static QueryHistoryEntry? Parse(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<QueryHistoryEntry>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
