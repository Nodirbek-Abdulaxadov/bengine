namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>A workspace tab: the fixed Properties and SQL tabs, or a closable Data or Query tab.</summary>
public abstract partial class DocumentViewModel : ObservableObject
{
    [ObservableProperty]
    private string _breadcrumb = string.Empty;

    [ObservableProperty]
    private string? _breadcrumbKind;

    /// <summary>Gets the tab title.</summary>
    public abstract string Title { get; }

    /// <summary>Gets the node kind whose icon the tab shows, or <c>null</c> for none.</summary>
    public virtual string? IconKind => null;

    /// <summary>Gets a value indicating whether the tab can be closed.</summary>
    public virtual bool CanClose => true;

    /// <summary>Gets a tooltip describing the tab.</summary>
    public virtual string? ToolTip => Title;

    /// <summary>Gets the color of the tab's server (<c>#rrggbb</c>), or <c>null</c>.</summary>
    public virtual string? AccentColor => null;

    /// <summary>Gets or sets the action that closes the tab (set by the explorer).</summary>
    public Action? Close { get; set; }
}

/// <summary>One property row: a name and a value (or, for a collection, a child and its details).</summary>
/// <param name="Name">The property name or child label.</param>
/// <param name="Value">The value or detail.</param>
public sealed record PropertyRow(string Name, string? Value);

/// <summary>
/// An object's Quick Documentation (Ctrl+Q): its properties, or its children
/// for a collection, and its DDL.
/// </summary>
public sealed partial class PropertiesDocumentViewModel : DocumentViewModel
{
    [ObservableProperty]
    private string _firstHeader = "Property";

    [ObservableProperty]
    private string _secondHeader = "Value";

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _sql;

    /// <summary>Gets or sets the dialect of the DDL.</summary>
    public Bengine.Enums.DatabaseProvider Provider { get; set; } = Bengine.Enums.DatabaseProvider.PostgreSql;

    /// <inheritdoc />
    public override string Title => "Quick Documentation";

    /// <summary>Gets the rows to show.</summary>
    public ObservableCollection<PropertyRow> Rows { get; } = [];
}

/// <summary>An object's DDL in a closable tab ("Go to DDL", Ctrl+B).</summary>
public sealed partial class SqlDocumentViewModel : DocumentViewModel
{
    private readonly string _title;
    private readonly string? _iconKind;
    private readonly string? _accent;

    [ObservableProperty]
    private string? _sql;

    [ObservableProperty]
    private string? _message;

    /// <summary>Creates the tab.</summary>
    /// <param name="title">The tab title (the object's name).</param>
    /// <param name="iconKind">The object's kind, for the tab icon.</param>
    /// <param name="key">Identifies the object, so a second Ctrl+B focuses the open tab.</param>
    /// <param name="accent">The server's color.</param>
    public SqlDocumentViewModel(string title = "SQL", string? iconKind = null, string? key = null, string? accent = null)
    {
        _title = title;
        _iconKind = iconKind;
        _accent = accent;
        Key = key;
    }

    /// <summary>Gets the object's key (<c>server|node</c>).</summary>
    public string? Key { get; }

    /// <summary>Gets the server's registration id, so the tab can be reopened after a restart.</summary>
    public string? ServerId { get; init; }

    /// <summary>Gets the database the object lives in.</summary>
    public string? Database { get; init; }

    /// <summary>Gets or sets the action that opens the SQL in a Query Tool (set by the explorer).</summary>
    public Action<string>? OpenInQueryTool { get; set; }

    /// <summary>Gets or sets the dialect of the shown SQL.</summary>
    public Bengine.Enums.DatabaseProvider Provider { get; set; } = Bengine.Enums.DatabaseProvider.PostgreSql;

    /// <inheritdoc />
    public override string Title => _title;

    /// <inheritdoc />
    public override string? IconKind => _iconKind;

    /// <inheritdoc />
    public override string? AccentColor => _accent;

    /// <inheritdoc />
    public override string? ToolTip => $"{Breadcrumb} — DDL";
}
