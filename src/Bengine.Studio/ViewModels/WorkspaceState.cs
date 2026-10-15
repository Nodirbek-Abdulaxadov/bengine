namespace Bengine.Studio.ViewModels;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// What the Studio window looked like when it was closed, restored the next
/// time it opens (<c>~/.config/bengine/workspace.json</c>): the window's
/// placement, the expanded and selected tree rows, and the open tabs —
/// consoles with their text (unsaved edits of SQL files included), data
/// editors with their filter and sort, DDL, activity, and diagrams.
/// Passwords and query results are never written.
/// </summary>
public sealed class WorkspaceState
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Gets or sets the window's placement, or <c>null</c> for the default.</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>Gets or sets the width of the side panel (Database / Files).</summary>
    public double? SidebarWidth { get; set; }

    /// <summary>Gets or sets the selected side panel: 0 for Database, 1 for Files.</summary>
    public int SidePanel { get; set; }

    /// <summary>Gets or sets the keys of the expanded tree rows (see <see cref="TreeItemViewModel.StateKey"/>).</summary>
    public List<string> Expanded { get; set; } = [];

    /// <summary>Gets or sets the key of the selected tree row, or <c>null</c>.</summary>
    public string? Selected { get; set; }

    /// <summary>Gets or sets the open tabs, in order.</summary>
    public List<TabState> Tabs { get; set; } = [];

    /// <summary>Gets or sets the index of the active tab, or -1.</summary>
    public int ActiveTab { get; set; } = -1;

    /// <summary>Reads the state; a missing or unreadable file gives an empty state.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The state.</returns>
    public static WorkspaceState Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Serializes the state.</summary>
    /// <returns>The JSON text.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>Where the window was and how large.</summary>
/// <param name="X">The left edge, in screen pixels.</param>
/// <param name="Y">The top edge, in screen pixels.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <param name="Maximized">Whether it was maximized.</param>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized);

/// <summary>One open tab, enough to reopen it.</summary>
public sealed record TabState
{
    /// <summary>Gets the tab kind: <c>query</c>, <c>data</c>, <c>ddl</c>, <c>activity</c>, or <c>diagram</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the server's registration id.</summary>
    public string? ServerId { get; init; }

    /// <summary>Gets the database.</summary>
    public string? Database { get; init; }

    /// <summary>Gets the console text, or the shown DDL.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the SQL file a console edits, or <c>null</c>.</summary>
    public string? FilePath { get; init; }

    /// <summary>Gets whether <see cref="Text"/> holds edits the file does not have yet.</summary>
    public bool Unsaved { get; init; }

    /// <summary>Gets the console's row limit.</summary>
    public int? MaxRows { get; init; }

    /// <summary>Gets the data editor's relation.</summary>
    public string? Table { get; init; }

    /// <summary>Gets the relation's (or the diagram's) schema.</summary>
    public string? Schema { get; init; }

    /// <summary>Gets whether the relation is a view.</summary>
    public bool IsView { get; init; }

    /// <summary>Gets the data editor's filter.</summary>
    public string? Where { get; init; }

    /// <summary>Gets the data editor's ordering.</summary>
    public string? OrderBy { get; init; }

    /// <summary>Gets the data editor's page size.</summary>
    public int? PageSize { get; init; }

    /// <summary>Gets a DDL tab's title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets a DDL tab's icon kind.</summary>
    public string? IconKind { get; init; }

    /// <summary>Gets a DDL tab's object key.</summary>
    public string? Key { get; init; }

    /// <summary>Gets a DDL tab's breadcrumb.</summary>
    public string? Breadcrumb { get; init; }
}
