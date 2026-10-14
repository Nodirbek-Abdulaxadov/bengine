namespace Bengine.Studio.Controls;

using Avalonia.Input;

/// <summary>An action the keymap can bind and the action palette can run.</summary>
/// <param name="Id">The stable id (<c>query.run</c>), the key in <c>studio.json</c>'s shortcut overrides.</param>
/// <param name="Title">What the palette shows.</param>
/// <param name="Category">The group: General, Files, Query, Editor, Explorer.</param>
/// <param name="Default">The default gestures, comma-separated (<c>Ctrl+Enter, F5</c>), or empty.</param>
public sealed record KeyAction(string Id, string Title, string Category, string Default);

/// <summary>
/// The Studio's keyboard shortcuts in one place, with per-user overrides from
/// the settings: every handler asks <see cref="Matches"/> instead of testing
/// keys itself, so a rebinding in the Keymap dialog takes effect everywhere.
/// Cmd on macOS counts as Ctrl.
/// </summary>
public sealed class Keymap
{
    /// <summary>Gets every bindable action.</summary>
    public static IReadOnlyList<KeyAction> Actions { get; } =
    [
        new("actions.find", "Find Action…", "General", "Ctrl+Shift+A"),
        new("keymap.edit", "Keymap…", "General", string.Empty),
        new("goto.object", "Go to Object…", "General", "Ctrl+N"),
        new("tab.close", "Close Tab", "General", "Ctrl+F4"),
        new("history.show", "Query History…", "General", "Ctrl+Alt+E"),
        new("compare.schema", "Compare Schemas…", "General", string.Empty),
        new("compare.data", "Compare Data…", "General", string.Empty),
        new("file.open", "Open SQL File…", "Files", "Ctrl+O"),
        new("file.save", "Save", "Files", "Ctrl+S"),
        new("file.saveAs", "Save As…", "Files", "Ctrl+Shift+S"),
        new("file.newScratch", "New Scratch File", "Files", "Ctrl+Alt+Shift+Insert"),
        new("file.localHistory", "Local History…", "Files", string.Empty),
        new("query.new", "New Query Tool", "Query", "Ctrl+Shift+Q"),
        new("query.run", "Run Statement", "Query", "Ctrl+Enter, F5"),
        new("query.runScript", "Run Script", "Query", "Ctrl+Shift+Enter"),
        new("query.cancel", "Cancel Statement", "Query", "Ctrl+F2"),
        new("query.explain", "Explain Plan", "Query", string.Empty),
        new("query.explainAnalyze", "Explain Analyze", "Query", string.Empty),
        new("query.commit", "Commit", "Query", string.Empty),
        new("query.rollback", "Rollback", "Query", string.Empty),
        new("editor.complete", "Code Completion", "Editor", "Ctrl+Space"),
        new("editor.format", "Reformat SQL", "Editor", "Ctrl+Alt+L, Ctrl+Shift+F"),
        new("editor.comment", "Comment Line", "Editor", "Ctrl+Oem2, Ctrl+Divide"),
        new("editor.duplicate", "Duplicate Line", "Editor", "Ctrl+D"),
        new("editor.toggleCase", "Toggle Case", "Editor", "Ctrl+Shift+U"),
        new("editor.replace", "Find and Replace", "Editor", "Ctrl+H"),
        new("tree.filter", "Filter the Tree", "Explorer", "Ctrl+F"),
        new("tree.refresh", "Refresh", "Explorer", "F5"),
        new("object.rename", "Rename…", "Explorer", "F2"),
        new("object.drop", "Drop…", "Explorer", "Delete"),
        new("object.findUsages", "Find Usages", "Explorer", "Alt+F7"),
        new("object.ddl", "Show DDL", "Explorer", "Ctrl+B"),
    ];

    private readonly Dictionary<string, IReadOnlyList<KeyGesture>> _gestures = new(StringComparer.Ordinal);

    /// <summary>Creates a keymap with overrides.</summary>
    /// <param name="overrides">Action id → gestures (empty disables the action's keys).</param>
    public Keymap(IReadOnlyDictionary<string, string>? overrides = null)
    {
        foreach (var action in Actions)
        {
            _gestures[action.Id] = Parse(overrides is not null && overrides.TryGetValue(action.Id, out var custom) ? custom : action.Default);
        }
    }

    /// <summary>Gets or sets the keymap in use.</summary>
    public static Keymap Current { get; set; } = new();

    /// <summary>Gets an action's gestures.</summary>
    /// <param name="id">The action id.</param>
    /// <returns>The gestures.</returns>
    public IReadOnlyList<KeyGesture> Gestures(string id) => _gestures.GetValueOrDefault(id) ?? [];

    /// <summary>Gets an action's gestures as text for menus and the palette (<c>Ctrl+/</c>).</summary>
    /// <param name="id">The action id.</param>
    /// <returns>The text, or empty.</returns>
    public string Display(string id) => string.Join(", ", Gestures(id).Select(Display));

    /// <summary>Gets an action's first gesture for a menu item, or <c>null</c>.</summary>
    /// <param name="id">The action id.</param>
    /// <returns>The gesture.</returns>
    public KeyGesture? MenuGesture(string id) => Gestures(id).FirstOrDefault();

    /// <summary>Tells whether a key press is one of an action's gestures.</summary>
    /// <param name="id">The action id.</param>
    /// <param name="e">The key press.</param>
    /// <returns><c>true</c> when it matches.</returns>
    public bool Matches(string id, KeyEventArgs e)
    {
        var modifiers = Normalize(e.KeyModifiers);
        return Gestures(id).Any(g => g.Key == e.Key && Normalize(g.KeyModifiers) == modifiers);
    }

    /// <summary>Finds the actions a gesture is bound to (for conflict warnings).</summary>
    /// <param name="gesture">The gesture.</param>
    /// <returns>The action ids.</returns>
    public IEnumerable<string> BoundTo(KeyGesture gesture) =>
        _gestures.Where(p => p.Value.Any(g => g.Key == gesture.Key && Normalize(g.KeyModifiers) == Normalize(gesture.KeyModifiers))).Select(p => p.Key);

    /// <summary>Parses comma-separated gestures, skipping invalid ones.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The gestures.</returns>
    public static IReadOnlyList<KeyGesture> Parse(string? text)
    {
        var gestures = new List<KeyGesture>();
        foreach (var part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                gestures.Add(KeyGesture.Parse(part));
            }
            catch (ArgumentException)
            {
                // An unknown key name is ignored.
            }
        }

        return gestures;
    }

    /// <summary>Formats a gesture the way it is typed in the settings.</summary>
    /// <param name="gesture">The gesture.</param>
    /// <returns>The text (<c>Ctrl+Shift+Enter</c>).</returns>
    public static string Format(KeyGesture gesture)
    {
        var parts = new List<string>();
        var modifiers = Normalize(gesture.KeyModifiers);
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(gesture.Key == Key.Enter ? "Enter" : gesture.Key.ToString());
        return string.Join('+', parts);
    }

    private static string Display(KeyGesture gesture) => Format(gesture)
        .Replace("OemQuestion", "/", StringComparison.Ordinal)
        .Replace("Oem2", "/", StringComparison.Ordinal)
        .Replace("Divide", "Num /", StringComparison.Ordinal)
        .Replace("Return", "Enter", StringComparison.Ordinal);

    private static KeyModifiers Normalize(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Meta) ? (modifiers & ~KeyModifiers.Meta) | KeyModifiers.Control : modifiers;
}
