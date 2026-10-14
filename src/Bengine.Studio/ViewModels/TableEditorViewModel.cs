namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Design;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>A column row of the table editor, editing a <see cref="ColumnDesign"/>.</summary>
/// <param name="design">The column design.</param>
public sealed partial class ColumnRow(ColumnDesign design) : ObservableObject
{
    /// <summary>Gets the edited design.</summary>
    public ColumnDesign Design => design;

    /// <summary>Gets or sets the name.</summary>
    public string Name { get => design.Name; set => Set(v => design.Name = v, value); }

    /// <summary>Gets or sets the type.</summary>
    public string Type { get => design.Type; set => Set(v => design.Type = v, value); }

    /// <summary>Gets or sets whether NULL is allowed.</summary>
    public bool Nullable { get => design.Nullable; set => Set(v => design.Nullable = v, value); }

    /// <summary>Gets or sets the default.</summary>
    public string Default { get => design.Default; set => Set(v => design.Default = v, value); }

    /// <summary>Gets or sets primary-key membership.</summary>
    public bool PrimaryKey { get => design.PrimaryKey; set => Set(v => design.PrimaryKey = v, value); }

    /// <summary>Gets or sets identity.</summary>
    public bool Identity { get => design.Identity; set => Set(v => design.Identity = v, value); }

    /// <summary>Gets or sets the comment.</summary>
    public string Comment { get => design.Comment; set => Set(v => design.Comment = v, value); }

    /// <summary>Gets the original name, shown when the column is renamed.</summary>
    public string? Renamed => design.OriginalName is { } original && original != design.Name ? $"was {original}" : design.OriginalName is null ? "new" : null;

    private void Set<T>(Action<T> apply, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        apply(value);
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(Renamed));
    }
}

/// <summary>
/// "Modify Table" / "New Table": edits a <see cref="TableDesign"/> on tabs for
/// columns, indexes, foreign keys, and checks, shows the resulting
/// <c>ALTER</c>/<c>CREATE</c> script live, and applies it in one transaction
/// (where the provider allows transactional DDL).
/// </summary>
public sealed partial class TableEditorViewModel : ObservableObject
{
    private readonly Func<Task<DatabaseSession>> _open;
    private readonly IDialogService? _dialogs;

    [ObservableProperty] private string _preview = string.Empty;
    [ObservableProperty] private string _problems = string.Empty;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ColumnRow? _selectedColumn;
    [ObservableProperty] private IndexDesign? _selectedIndex;
    [ObservableProperty] private ForeignKeyDesign? _selectedForeignKey;
    [ObservableProperty] private CheckDesign? _selectedCheck;

    /// <summary>Creates the editor.</summary>
    /// <param name="design">The design to edit.</param>
    /// <param name="open">Opens a session on the table's database.</param>
    /// <param name="dialogs">The dialogs.</param>
    /// <param name="readOnly">Whether the server is read-only (preview only).</param>
    public TableEditorViewModel(TableDesign design, Func<Task<DatabaseSession>> open, IDialogService? dialogs, bool readOnly)
    {
        Design = design;
        _open = open;
        _dialogs = dialogs;
        IsReadOnly = readOnly;
        foreach (var column in design.Columns)
        {
            Columns.Add(Watch(new ColumnRow(column)));
        }

        Indexes = new ObservableCollection<IndexDesign>(design.Indexes);
        ForeignKeys = new ObservableCollection<ForeignKeyDesign>(design.ForeignKeys);
        Checks = new ObservableCollection<CheckDesign>(design.Checks);
        Refresh();
    }

    /// <summary>Gets the design.</summary>
    public TableDesign Design { get; }

    /// <summary>Gets the window title.</summary>
    public string Title => Design.IsNew ? "New Table" : $"Modify Table — {Design.Original!.Name}";

    /// <summary>Gets a value indicating whether changes cannot be applied.</summary>
    public bool IsReadOnly { get; }

    /// <summary>Gets or sets the table name.</summary>
    public string Name
    {
        get => Design.Name;
        set
        {
            Design.Name = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    /// <summary>Gets or sets the table comment.</summary>
    public string Comment
    {
        get => Design.Comment;
        set
        {
            Design.Comment = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    /// <summary>Gets the column rows.</summary>
    public ObservableCollection<ColumnRow> Columns { get; } = [];

    /// <summary>Gets the indexes.</summary>
    public ObservableCollection<IndexDesign> Indexes { get; }

    /// <summary>Gets the foreign keys.</summary>
    public ObservableCollection<ForeignKeyDesign> ForeignKeys { get; }

    /// <summary>Gets the checks.</summary>
    public ObservableCollection<CheckDesign> Checks { get; }

    /// <summary>Gets the referential actions.</summary>
    public IReadOnlyList<ReferentialAction> Actions { get; } = Enum.GetValues<ReferentialAction>();

    /// <summary>Gets common type spellings for the provider, offered while typing.</summary>
    public IReadOnlyList<string> TypeSuggestions => Design.Provider switch
    {
        DatabaseProvider.PostgreSql => ["bigint", "integer", "smallint", "numeric(12,2)", "real", "double precision", "boolean", "text", "varchar(255)", "char(1)", "uuid", "date", "timestamp", "timestamptz", "time", "interval", "jsonb", "json", "bytea", "inet", "text[]", "integer[]"],
        DatabaseProvider.MySql => ["bigint", "int", "smallint", "tinyint(1)", "decimal(12,2)", "double", "float", "varchar(255)", "char(1)", "text", "longtext", "date", "datetime", "timestamp", "time", "json", "blob", "binary(16)", "enum('a','b')"],
        DatabaseProvider.SqlServer => ["bigint", "int", "smallint", "tinyint", "bit", "decimal(12,2)", "float", "nvarchar(255)", "nvarchar(max)", "varchar(255)", "char(1)", "date", "datetime2", "datetimeoffset", "time", "uniqueidentifier", "varbinary(max)"],
        DatabaseProvider.Oracle => ["NUMBER(19)", "NUMBER(10)", "NUMBER(12,2)", "NUMBER(1)", "VARCHAR2(255)", "VARCHAR2(4000)", "CHAR(1)", "CLOB", "BLOB", "DATE", "TIMESTAMP", "TIMESTAMP WITH TIME ZONE", "BINARY_DOUBLE", "RAW(16)", "INTERVAL DAY TO SECOND", "JSON", "XMLTYPE"],
        _ => ["INTEGER", "REAL", "TEXT", "BLOB", "NUMERIC", "VARCHAR(255)", "BOOLEAN", "DATETIME"],
    };

    /// <summary>Raised when the editor should close; the argument tells whether the table changed.</summary>
    public event Action<bool>? Closed;

    /// <summary>Re-renders the preview from the current design.</summary>
    public void Refresh()
    {
        Design.Indexes.Clear();
        Design.Indexes.AddRange(Indexes);
        Design.ForeignKeys.Clear();
        Design.ForeignKeys.AddRange(ForeignKeys);
        Design.Checks.Clear();
        Design.Checks.AddRange(Checks);
        Design.Columns.Clear();
        Design.Columns.AddRange(Columns.Select(c => c.Design));

        var problems = Design.Validate();
        Problems = string.Join("\n", problems);
        if (problems.Count > 0)
        {
            Preview = "-- " + string.Join("\n-- ", problems);
            return;
        }

        try
        {
            var script = Design.Script();
            Preview = script.Count == 0 ? "-- No changes." : string.Join("\n", script);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            Preview = "-- " + ex.Message;
        }
    }

    [RelayCommand]
    private void AddColumn()
    {
        var row = Watch(new ColumnRow(new ColumnDesign { Name = $"column{Columns.Count + 1}", Type = TypeSuggestions[0] }));
        Columns.Add(row);
        SelectedColumn = row;
        Refresh();
    }

    [RelayCommand]
    private void RemoveColumn()
    {
        if (SelectedColumn is { } row)
        {
            Columns.Remove(row);
            Refresh();
        }
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    [RelayCommand]
    private void AddIndex()
    {
        var index = new IndexDesign { Name = $"ix_{Design.Name}_{Indexes.Count + 1}", Columns = SelectedColumn?.Name ?? string.Empty };
        Indexes.Add(index);
        SelectedIndex = index;
        Refresh();
    }

    [RelayCommand]
    private void RemoveIndex()
    {
        if (SelectedIndex is { } index)
        {
            Indexes.Remove(index);
            Refresh();
        }
    }

    [RelayCommand]
    private void AddForeignKey()
    {
        var key = new ForeignKeyDesign { Name = $"fk_{Design.Name}_{ForeignKeys.Count + 1}", Columns = SelectedColumn?.Name ?? string.Empty, ReferencedColumns = "id" };
        ForeignKeys.Add(key);
        SelectedForeignKey = key;
        Refresh();
    }

    [RelayCommand]
    private void RemoveForeignKey()
    {
        if (SelectedForeignKey is { } key)
        {
            ForeignKeys.Remove(key);
            Refresh();
        }
    }

    [RelayCommand]
    private void AddCheck()
    {
        var check = new CheckDesign { Name = $"ck_{Design.Name}_{Checks.Count + 1}" };
        Checks.Add(check);
        SelectedCheck = check;
        Refresh();
    }

    [RelayCommand]
    private void RemoveCheck()
    {
        if (SelectedCheck is { } check)
        {
            Checks.Remove(check);
            Refresh();
        }
    }

    [RelayCommand]
    private async Task CopySqlAsync()
    {
        if (_dialogs is not null)
        {
            await _dialogs.SetClipboardAsync(Preview);
            Status = "Script copied.";
        }
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(false);

    [RelayCommand]
    private async Task ApplyAsync()
    {
        Refresh();
        if (IsReadOnly || Problems.Length > 0)
        {
            Status = IsReadOnly ? "The server is read-only." : Problems;
            IsError = true;
            return;
        }

        IsBusy = true;
        try
        {
            await using var session = await _open();
            var changes = Design.Changes();
            if (changes.Count == 0)
            {
                Closed?.Invoke(false);
                return;
            }

            var source = Design.Original is { } original ? new Bengine.Models.DatabaseSchema { Name = "source", Tables = [original] } : null;
            var target = new Bengine.Models.DatabaseSchema { Name = "target", Tables = [Design.ToTable()] };
            var statements = await SchemaApplier.RenderAsync(session, changes, source, target);
            if (changes.Any(c => c.IsDestructive) && _dialogs is not null
                && !await _dialogs.ConfirmAsync("Destructive Change", "The script drops or narrows columns and can lose data:\n\n" +
                    string.Join("\n", changes.Where(c => c.IsDestructive).Select(c => "• " + c.Description)) + "\n\nApply it?"))
            {
                return;
            }

            await SchemaApplier.ApplyAsync(session, statements);
            Closed?.Invoke(true);
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
            IsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Move(int delta)
    {
        if (SelectedColumn is not { } row)
        {
            return;
        }

        var index = Columns.IndexOf(row);
        var to = index + delta;
        if (to < 0 || to >= Columns.Count)
        {
            return;
        }

        Columns.Move(index, to);
        SelectedColumn = row;
        Refresh();
    }

    private ColumnRow Watch(ColumnRow row)
    {
        row.PropertyChanged += (_, _) => Refresh();
        return row;
    }
}
