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

    /// <summary>Gets or sets the action that closes the tab (set by the explorer).</summary>
    public Action? Close { get; set; }
}

/// <summary>One property row: a name and a value (or, for a collection, a child and its details).</summary>
/// <param name="Name">The property name or child label.</param>
/// <param name="Value">The value or detail.</param>
public sealed record PropertyRow(string Name, string? Value);

/// <summary>The Properties tab: the selected node's properties, or its children for a collection.</summary>
public sealed partial class PropertiesDocumentViewModel : DocumentViewModel
{
    [ObservableProperty]
    private string _firstHeader = "Property";

    [ObservableProperty]
    private string _secondHeader = "Value";

    [ObservableProperty]
    private string? _message = "Select an object in the Object Explorer.";

    [ObservableProperty]
    private bool _isLoading;

    /// <inheritdoc />
    public override string Title => "Properties";

    /// <inheritdoc />
    public override bool CanClose => false;

    /// <summary>Gets the rows to show.</summary>
    public ObservableCollection<PropertyRow> Rows { get; } = [];
}

/// <summary>The SQL tab: the selected object's DDL.</summary>
public sealed partial class SqlDocumentViewModel : DocumentViewModel
{
    [ObservableProperty]
    private string? _sql;

    [ObservableProperty]
    private string? _message = "Select an object to see its SQL.";

    /// <summary>Gets or sets the action that opens the SQL in a Query Tool (set by the explorer).</summary>
    public Action<string>? OpenInQueryTool { get; set; }

    /// <inheritdoc />
    public override string Title => "SQL";

    /// <inheritdoc />
    public override bool CanClose => false;
}
