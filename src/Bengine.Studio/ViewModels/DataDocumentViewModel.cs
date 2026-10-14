namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Data;
using Bengine.Introspection.Export;
using Bengine.Introspection.Model;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Sql;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>What a data editor needs from the explorer.</summary>
/// <param name="Server">The server.</param>
/// <param name="Database">The database.</param>
/// <param name="OpenSession">Opens a session on the database.</param>
/// <param name="Catalog">Gets the database's metadata (for keys and foreign keys).</param>
/// <param name="OpenRelated">Opens another relation's data filtered by a <c>WHERE</c> condition (foreign-key navigation).</param>
/// <param name="OpenConsole">Opens a Query Tool with SQL.</param>
/// <param name="Dialogs">The dialogs.</param>
public sealed record DataContext(
    ServerProfile Server,
    string? Database,
    Func<Task<DatabaseSession>> OpenSession,
    Func<bool, Task<DatabaseCatalog?>> Catalog,
    Action<TableRef, string> OpenRelated,
    Action<string> OpenConsole,
    IDialogService? Dialogs);

/// <summary>A foreign key as the data editor offers it for navigation.</summary>
/// <param name="Label">The menu text.</param>
/// <param name="Key">The key.</param>
/// <param name="Outgoing">Whether the key is declared on this relation (it references another).</param>
public sealed record ForeignKeyLink(string Label, CatalogForeignKey Key, bool Outgoing);

/// <summary>
/// A "View/Edit Data" tab for one relation, modeled on a database IDE's data
/// editor. Rows are read a page at a time with a free-form <c>WHERE</c> and
/// <c>ORDER BY</c>; edits — cell changes, <c>NULL</c>/<c>DEFAULT</c>, new and
/// cloned rows, deletions, pasted blocks — are held as pending changes, shown
/// in color, previewed as SQL, and submitted together in one transaction (or
/// reverted). Foreign keys navigate to the referenced or referencing rows.
/// Every operation opens a short-lived session.
/// </summary>
public sealed partial class DataDocumentViewModel : DocumentViewModel
{
    private static readonly string[] NumericTypes =
    [
        "smallint", "integer", "bigint", "int", "int2", "int4", "int8", "tinyint", "mediumint", "numeric", "decimal",
        "real", "double", "float", "money", "smallmoney", "serial", "bigserial",
    ];

    private readonly DataContext _context;
    private IReadOnlyList<string> _primaryKey = [];
    private CatalogRelation? _relation;
    private IReadOnlyList<RowItem> _selection = [];

    [ObservableProperty] private string _whereText = string.Empty;
    [ObservableProperty] private string _orderByText = string.Empty;
    [ObservableProperty] private int _pageSize = 100;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private long? _totalCount;
    [ObservableProperty] private string _pageInfo = string.Empty;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private RowItem? _selectedRow;
    [ObservableProperty] private int _currentColumn = -1;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private string _aggregates = string.Empty;
    [ObservableProperty] private string _findText = string.Empty;
    [ObservableProperty] private bool _isRecordView;
    [ObservableProperty] private bool _autoSubmit;
    [ObservableProperty] private string _pageSql = string.Empty;

    /// <summary>Creates the tab.</summary>
    /// <param name="context">The explorer context.</param>
    /// <param name="table">The relation.</param>
    /// <param name="where">An initial <c>WHERE</c> condition, or <c>null</c>.</param>
    public DataDocumentViewModel(DataContext context, TableRef table, string? where = null)
    {
        _context = context;
        Table = table;
        _whereText = where ?? string.Empty;
        Rows.CollectionChanged += (_, _) => UpdatePending();
    }

    /// <summary>Gets the relation shown in the tab.</summary>
    public TableRef Table { get; }

    /// <summary>Gets the server.</summary>
    public ServerProfile Server => _context.Server;

    /// <summary>Gets the "server / database" label.</summary>
    public string Location => $"{Server.Name} / {_context.Database}";

    /// <summary>Gets the provider.</summary>
    public DatabaseProvider Provider => Server.Provider;

    /// <inheritdoc />
    public override string Title => Table.Name + (PendingCount > 0 ? " *" : string.Empty);

    /// <inheritdoc />
    public override string IconKind => Table.Kind == TableKind.View ? "View" : "Table";

    /// <inheritdoc />
    public override string ToolTip => $"View/Edit Data — {Location} / {Table.QualifiedName}";

    /// <inheritdoc />
    public override string? AccentColor => Server.Color;

    /// <summary>Gets a value indicating whether rows can be changed (tables on writable servers).</summary>
    public bool CanEdit => !Server.ReadOnly && Table.Kind == TableKind.Table;

    /// <summary>Gets the page-size choices.</summary>
    public IReadOnlyList<int> PageSizes { get; } = [25, 50, 100, 250, 500, 1000];

    /// <summary>Gets the result column names.</summary>
    public ObservableCollection<string> Columns { get; } = [];

    /// <summary>Gets the column type names, aligned with <see cref="Columns"/>.</summary>
    public IReadOnlyList<string> ColumnTypes { get; private set; } = [];

    /// <summary>Gets the rows of the current page (new rows first).</summary>
    public ObservableCollection<RowItem> Rows { get; } = [];

    /// <summary>Gets the rows shown: all rows, or those containing the find text.</summary>
    public IEnumerable<RowItem> VisibleRows => string.IsNullOrEmpty(FindText)
        ? Rows
        : Rows.Where(r => Enumerable.Range(0, Columns.Count).Any(i => r[i]?.Contains(FindText, StringComparison.OrdinalIgnoreCase) == true));

    /// <summary>Gets the selected row as name/value pairs, for the record (transposed) view.</summary>
    public ObservableCollection<RecordField> Record { get; } = [];

    /// <summary>Gets the foreign keys the current column takes part in, for navigation.</summary>
    public IReadOnlyList<ForeignKeyLink> Links { get; private set; } = [];

    /// <summary>Gets the primary-key columns.</summary>
    public IReadOnlyList<string> PrimaryKey => _primaryKey;

    /// <summary>Raised when the column set changes: names, display types, primary-key and foreign-key columns.</summary>
    public event Action<IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyList<string>>? ColumnsChanged;

    /// <summary>Gets whether there are pending changes.</summary>
    public bool HasPending => PendingCount > 0;

    /// <summary>Loads the relation's metadata and its first page.</summary>
    /// <returns>A task that completes when the page is shown.</returns>
    public async Task InitializeAsync()
    {
        try
        {
            if (await _context.Catalog(false) is { } catalog)
            {
                _relation = catalog.Find(Table.Schema, Table.Name);
                _primaryKey = _relation?.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList() ?? [];
                _incoming = _relation is null ? [] : catalog.Incoming(_relation).ToList();
                _outgoing = _relation is null ? [] : catalog.Outgoing(_relation).ToList();
            }
        }
        catch (IntrospectionException ex)
        {
            Status = $"Metadata unavailable ({ex.Message}); edits use every column as the row key.";
        }

        await RunAsync(LoadPageAsync);
    }

    private IReadOnlyList<CatalogForeignKey> _incoming = [];
    private IReadOnlyList<CatalogForeignKey> _outgoing = [];

    /// <summary>Records the grid selection (rows and current column) for commands and aggregates.</summary>
    /// <param name="rows">The selected rows.</param>
    /// <param name="column">The current column position.</param>
    public void SetSelection(IReadOnlyList<RowItem> rows, int column)
    {
        _selection = rows;
        CurrentColumn = column;
        Aggregates = Summarize(rows, column);
        Links = column < 0 || column >= Columns.Count ? [] : LinksFor(Columns[column]);
        OnPropertyChanged(nameof(Links));
        if (IsRecordView)
        {
            FillRecord();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (await ConfirmDiscardAsync())
        {
            await RunAsync(LoadPageAsync);
        }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (HasMore && await ConfirmDiscardAsync())
        {
            Page++;
            await RunAsync(LoadPageAsync);
        }
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (Page > 1 && await ConfirmDiscardAsync())
        {
            Page--;
            await RunAsync(LoadPageAsync);
        }
    }

    [RelayCommand]
    private async Task FirstPageAsync()
    {
        if (await ConfirmDiscardAsync())
        {
            Page = 1;
            await RunAsync(LoadPageAsync);
        }
    }

    [RelayCommand]
    private async Task LastPageAsync()
    {
        if (TotalCount is { } total && await ConfirmDiscardAsync())
        {
            Page = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
            await RunAsync(LoadPageAsync);
        }
    }

    [RelayCommand]
    private async Task ApplyFilterAsync()
    {
        if (await ConfirmDiscardAsync())
        {
            Page = 1;
            await RunAsync(LoadPageAsync);
        }
    }

    [RelayCommand]
    private async Task ClearFilterAsync()
    {
        WhereText = string.Empty;
        OrderByText = string.Empty;
        await ApplyFilterAsync();
    }

    /// <summary>Adds "<c>column = value</c>" (or <c>IS NULL</c>) for the current cell to the <c>WHERE</c> box and applies it.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column position.</param>
    /// <param name="op">The operator: <c>=</c>, <c>&lt;&gt;</c>, <c>&lt;</c>, <c>&gt;</c>, or <c>LIKE</c>.</param>
    /// <returns>A task that completes when the page is reloaded.</returns>
    public Task FilterByValueAsync(RowItem row, int column, string op)
    {
        var name = SqlKeywords.QuoteIfNeeded(Columns[column], Provider);
        var value = row.CurrentValue(column);
        var condition = value is null
            ? op == "<>" ? $"{name} IS NOT NULL" : $"{name} IS NULL"
            : op == "LIKE" ? $"{name} LIKE {SqlLiterals.Format("%" + RowItem.Format(value) + "%", Provider)}"
            : $"{name} {op} {Literal(column, value)}";
        WhereText = string.IsNullOrWhiteSpace(WhereText) ? condition : $"{WhereText.Trim()} AND {condition}";
        return ApplyFilterAsync();
    }

    /// <summary>Sorts by a column: ascending, then descending, then unsorted.</summary>
    /// <param name="column">The column whose header was clicked.</param>
    /// <returns>A task that completes when the page is reloaded.</returns>
    public Task SortByAsync(string column)
    {
        var name = SqlKeywords.QuoteIfNeeded(column, Provider);
        OrderByText = OrderByText.Trim() == $"{name} ASC" ? $"{name} DESC" : OrderByText.Trim() == $"{name} DESC" ? string.Empty : $"{name} ASC";
        return ApplyFilterAsync();
    }

    [RelayCommand]
    private void AddRow()
    {
        if (!RequireEditable())
        {
            return;
        }

        var row = RowItem.Blank(Columns.ToList());
        Watch(row);
        Rows.Insert(0, row);
        SelectedRow = row;
        Status = "New row added — fill it in, then Submit (Ctrl+Enter). Untouched cells take their DEFAULT.";
        IsError = false;
    }

    [RelayCommand]
    private void CloneRow()
    {
        if (!RequireEditable() || SelectedRow is not { } source)
        {
            return;
        }

        // Generated keys (identity, serial, autoincrement) are left to the database.
        var generated = _relation?.Columns.Where(c => c.IsPrimaryKey && c.HasDefault || c.IsGenerated).Select(c => c.Name) ?? [];
        var row = RowItem.CloneOf(source, generated);
        Watch(row);
        Rows.Insert(Rows.IndexOf(source) + 1, row);
        SelectedRow = row;
        Status = "Row cloned — adjust it, then Submit.";
    }

    [RelayCommand]
    private void DeleteRows()
    {
        if (!RequireEditable())
        {
            return;
        }

        var rows = _selection.Count > 0 ? _selection : SelectedRow is { } one ? [one] : [];
        foreach (var row in rows.ToList())
        {
            if (row.IsNew)
            {
                Rows.Remove(row);
            }
            else
            {
                row.MarkDeleted(!row.IsDeleted);
            }
        }

        UpdatePending();
    }

    [RelayCommand]
    private void SetNull() => ForSelectedCells(row => row.SetNull(CurrentColumn));

    [RelayCommand]
    private void SetDefault() => ForSelectedCells(row => row.SetDefault(CurrentColumn));

    [RelayCommand]
    private void RevertSelected()
    {
        var rows = _selection.Count > 0 ? _selection : SelectedRow is { } one ? [one] : [];
        foreach (var row in rows.ToList())
        {
            if (row.IsNew)
            {
                Rows.Remove(row);
            }
            else
            {
                row.Revert();
            }
        }

        UpdatePending();
    }

    [RelayCommand]
    private void RevertAll()
    {
        foreach (var row in Rows.Where(r => r.IsNew).ToList())
        {
            Rows.Remove(row);
        }

        foreach (var row in Rows)
        {
            row.Revert();
        }

        UpdatePending();
        Status = "Pending changes reverted.";
        IsError = false;
    }

    [RelayCommand]
    private async Task PreviewChangesAsync()
    {
        var changes = BuildChanges();
        if (changes.Count == 0)
        {
            Status = "No pending changes.";
            return;
        }

        var sql = string.Join(Environment.NewLine, RelationalDataWriter.Preview(Provider, Table, changes));
        if (_context.Dialogs is not null && await _context.Dialogs.ReviewSqlAsync($"Pending Changes — {Table.QualifiedName}", sql, "Submit"))
        {
            await SubmitAsync();
        }
    }

    [RelayCommand]
    private Task SubmitAsync()
    {
        var changes = BuildChanges();
        if (changes.Count == 0)
        {
            Status = "No pending changes.";
            return Task.CompletedTask;
        }

        return RunAsync(async session =>
        {
            var counts = await session.Writer.ApplyAsync(Table, changes);
            await LoadPageAsync(session);
            Status = $"Submitted {changes.Count} change(s): " +
                     $"{changes.Count(c => c.Kind == RowChangeKind.Insert)} inserted, " +
                     $"{changes.Count(c => c.Kind == RowChangeKind.Update)} updated, " +
                     $"{changes.Count(c => c.Kind == RowChangeKind.Delete)} deleted ({counts.Sum():N0} rows).";
        });
    }

    /// <summary>Called after a cell edit is committed in the grid: submits at once in auto-submit mode.</summary>
    /// <returns>A task that completes when done.</returns>
    public Task CellEditedAsync()
    {
        UpdatePending();
        return AutoSubmit && PendingCount > 0 && !Rows.Any(r => r.IsNew) ? SubmitAsync() : Task.CompletedTask;
    }

    /// <summary>Pastes tab- or comma-separated text into the grid, starting at a cell; extra lines become new rows.</summary>
    /// <param name="text">The clipboard text.</param>
    /// <param name="startRow">The row of the top-left target cell.</param>
    /// <param name="startColumn">The column of the top-left target cell.</param>
    public void Paste(string text, RowItem startRow, int startColumn)
    {
        if (!RequireEditable() || startColumn < 0)
        {
            return;
        }

        var lines = CsvText.Parse(text);
        var index = Rows.IndexOf(startRow);
        foreach (var cells in lines)
        {
            RowItem row;
            if (index >= 0 && index < Rows.Count)
            {
                row = Rows[index];
            }
            else
            {
                row = RowItem.Blank(Columns.ToList());
                Watch(row);
                Rows.Add(row);
            }

            for (var c = 0; c < cells.Count && startColumn + c < Columns.Count; c++)
            {
                row[startColumn + c] = cells[c];
            }

            index++;
        }

        UpdatePending();
        Status = $"Pasted {lines.Count} row(s) — Submit to save.";
    }

    /// <summary>Formats rows for the clipboard or a file.</summary>
    /// <param name="format">csv, tsv, json, markdown, sql-insert, sql-update, or where.</param>
    /// <param name="rows">The rows; <c>null</c> for the selection (or the whole page).</param>
    /// <returns>The text.</returns>
    public string Format(string format, IReadOnlyList<RowItem>? rows = null)
    {
        rows ??= _selection.Count > 0 ? _selection : Rows.ToList();
        var values = rows.Select(r => r.CurrentValues()).ToList();
        var columns = Columns.ToList();
        return format switch
        {
            "tsv" => DataExporter.ToTsv(columns, values),
            "json" => DataExporter.ToJson(columns, values),
            "markdown" => DataExporter.ToMarkdown(columns, values),
            "sql-insert" => DataExporter.ToSqlInserts(Provider, Table, columns, values),
            "sql-update" => string.Join(Environment.NewLine, RelationalDataWriter.Preview(Provider, Table,
                rows.Select(r => RowChange.Update(KeyOf(r), columns.Select((c, i) => (c, i)).Where(x => !_primaryKey.Contains(x.c, StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(x => x.c, x => Convert(x.i, r.CurrentValue(x.i))))).ToList())) + Environment.NewLine,
            "where" => string.Join(" OR ", rows.Select(r => "(" + string.Join(" AND ", KeyOf(r).Select(k =>
                k.Value is null ? $"{SqlKeywords.QuoteIfNeeded(k.Key, Provider)} IS NULL" : $"{SqlKeywords.QuoteIfNeeded(k.Key, Provider)} = {SqlLiterals.Format(k.Value, Provider)}")) + ")")),
            _ => DataExporter.ToCsv(columns, values),
        };
    }

    [RelayCommand]
    private async Task ExportAsync(string format)
    {
        var extension = format switch { "markdown" => "md", "sql-insert" => "sql", "xlsx" => "xlsx", _ => format };
        var path = _context.Dialogs is null ? null : await _context.Dialogs.SaveFileAsync($"{Table.Name}.{extension}");
        if (path is null)
        {
            return;
        }

        await RunAsync(async session =>
        {
            var page = await session.Data.QueryAsync(Table, Query(page: 1, pageSize: DataQuery.MaxPageSize, count: false));
            if (format == "xlsx")
            {
                await File.WriteAllBytesAsync(path, XlsxWriter.Write(Table.Name, page.Columns, page.Rows));
            }
            else
            {
                var text = format switch
                {
                    "json" => DataExporter.ToJson(page.Columns, page.Rows),
                    "sql-insert" => DataExporter.ToSqlInserts(Provider, Table, page.Columns, page.Rows),
                    "tsv" => DataExporter.ToTsv(page.Columns, page.Rows),
                    "markdown" => DataExporter.ToMarkdown(page.Columns, page.Rows),
                    _ => DataExporter.ToCsv(page.Columns, page.Rows),
                };
                await File.WriteAllTextAsync(path, text);
            }

            Status = $"Exported {page.Rows.Count:N0} row(s) to {path}" + (page.HasMore ? $" (first {DataQuery.MaxPageSize:N0})" : string.Empty);
        });
    }

    [RelayCommand]
    private void OpenInConsole() => _context.OpenConsole(PageSql + ";");

    /// <summary>Opens the rows a foreign key leads to from a row.</summary>
    /// <param name="link">The key.</param>
    /// <param name="row">The row.</param>
    public void Navigate(ForeignKeyLink link, RowItem row)
    {
        var (sourceColumns, targetColumns, target) = link.Outgoing
            ? (link.Key.Columns, link.Key.ReferencedColumns, new TableRef(link.Key.ReferencedTable, link.Key.ReferencedSchema))
            : (link.Key.ReferencedColumns, link.Key.Columns, new TableRef(link.Key.Table, link.Key.Schema));
        var conditions = new List<string>();
        for (var i = 0; i < sourceColumns.Count; i++)
        {
            var index = Columns.ToList().FindIndex(c => c.Equals(sourceColumns[i], StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            var value = row.CurrentValue(index);
            var name = SqlKeywords.QuoteIfNeeded(targetColumns[i], Provider);
            conditions.Add(value is null ? $"{name} IS NULL" : $"{name} = {Literal(index, value)}");
        }

        _context.OpenRelated(target, string.Join(" AND ", conditions));
    }

    /// <summary>Opens a large editor for the current cell and applies the edited value.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column position.</param>
    /// <returns>A task that completes when the editor closes.</returns>
    public async Task EditValueAsync(RowItem row, int column)
    {
        if (_context.Dialogs is null || column < 0 || column >= Columns.Count)
        {
            return;
        }

        var edited = await _context.Dialogs.EditValueAsync($"{Table.Name}.{Columns[column]}", row[column], readOnly: !CanEdit || row.IsDeleted);
        if (edited is not null && CanEdit && edited != row[column])
        {
            row[column] = edited;
            await CellEditedAsync();
        }
    }

    partial void OnFindTextChanged(string value) => OnPropertyChanged(nameof(VisibleRows));

    partial void OnPageSizeChanged(int value) => _ = ApplyFilterAsync();

    partial void OnIsRecordViewChanged(bool value)
    {
        if (value)
        {
            FillRecord();
        }
    }

    partial void OnPendingCountChanged(int value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasPending));
    }

    partial void OnSelectedRowChanged(RowItem? value)
    {
        if (IsRecordView)
        {
            FillRecord();
        }
    }

    private void FillRecord()
    {
        Record.Clear();
        if (SelectedRow is not { } row)
        {
            return;
        }

        for (var i = 0; i < Columns.Count; i++)
        {
            Record.Add(new RecordField(row, i, Columns[i], i < ColumnTypes.Count ? ColumnTypes[i] : string.Empty));
        }
    }

    private async Task LoadPageAsync(DatabaseSession session)
    {
        var query = Query(Page, PageSize, count: true);
        var result = await session.Data.QueryAsync(Table, query);
        PageSql = result.Sql;
        ColumnTypes = result.ColumnTypes;
        if (!Columns.SequenceEqual(result.Columns))
        {
            Columns.Clear();
            foreach (var column in result.Columns)
            {
                Columns.Add(column);
            }

            var fkColumns = _outgoing.SelectMany(k => k.Columns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            ColumnsChanged?.Invoke(result.Columns, result.ColumnTypes, _primaryKey, fkColumns);
        }

        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
        foreach (var values in result.Rows)
        {
            var row = new RowItem(result.Columns, values);
            Watch(row);
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(VisibleRows));
        TotalCount = result.TotalCount;
        HasMore = result.HasMore;
        var from = result.Rows.Count == 0 ? 0 : query.Offset + 1;
        var to = query.Offset + result.Rows.Count;
        var pages = TotalCount is { } total ? Math.Max(1, (int)Math.Ceiling(total / (double)PageSize)) : (int?)null;
        PageInfo = $"{from:N0}–{to:N0}" + (TotalCount is { } t ? $" of {t:N0}" : string.Empty) + $"  ·  page {Page}" + (pages is { } p ? $" / {p}" : string.Empty);
        UpdatePending();
    }

    private DataQuery Query(int page, int pageSize, bool count) => new()
    {
        Page = page,
        PageSize = pageSize,
        IncludeTotalCount = count,
        WhereSql = string.IsNullOrWhiteSpace(WhereText) ? null : WhereText,
        OrderBySql = string.IsNullOrWhiteSpace(OrderByText) ? null : OrderByText,
    };

    private List<RowChange> BuildChanges()
    {
        var changes = new List<RowChange>();
        foreach (var row in Rows.Where(r => r.IsDeleted && !r.IsNew))
        {
            changes.Add(RowChange.Delete(KeyOf(row)));
        }

        foreach (var row in Rows.Where(r => r is { IsNew: false, IsDeleted: false, HasChanges: true }))
        {
            var values = Enumerable.Range(0, Columns.Count).Where(row.IsChanged)
                .ToDictionary(i => Columns[i], i => Convert(i, row.CurrentValue(i)));
            changes.Add(RowChange.Update(KeyOf(row), values));
        }

        foreach (var row in Rows.Where(r => r.IsNew).Reverse())
        {
            var values = Enumerable.Range(0, Columns.Count).ToDictionary(i => Columns[i], i => Convert(i, row.CurrentValue(i)));
            changes.Add(RowChange.Insert(values));
        }

        return changes;
    }

    private Dictionary<string, object?> KeyOf(RowItem row)
    {
        var columns = _primaryKey.Count > 0 ? _primaryKey : row.Columns;
        return columns.ToDictionary(c => c, row.OriginalValue);
    }

    // Typed text for numeric columns is sent as a number; everything else as text the server coerces.
    private object? Convert(int column, object? value)
    {
        if (value is not string text || column >= ColumnTypes.Count)
        {
            return value;
        }

        var type = ColumnTypes[column].Split('(')[0].Trim();
        return NumericTypes.Any(n => type.Equals(n, StringComparison.OrdinalIgnoreCase))
            && decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)
            ? number
            : text;
    }

    private string Literal(int column, object value) => SqlLiterals.Format(Convert(column, value is string s ? s : value), Provider);

    private IReadOnlyList<ForeignKeyLink> LinksFor(string column)
    {
        var links = new List<ForeignKeyLink>();
        foreach (var key in _outgoing.Where(k => k.Columns.Contains(column, StringComparer.OrdinalIgnoreCase)))
        {
            links.Add(new ForeignKeyLink($"Go to {key.ReferencedTable} ({string.Join(", ", key.ReferencedColumns)})", key, Outgoing: true));
        }

        foreach (var key in _incoming.Where(k => k.ReferencedColumns.Contains(column, StringComparer.OrdinalIgnoreCase)))
        {
            links.Add(new ForeignKeyLink($"Rows of {key.Table} referencing this ({string.Join(", ", key.Columns)})", key, Outgoing: false));
        }

        return links;
    }

    private void ForSelectedCells(Action<RowItem> action)
    {
        if (!RequireEditable() || CurrentColumn < 0)
        {
            return;
        }

        foreach (var row in _selection.Count > 0 ? _selection : SelectedRow is { } one ? [one] : [])
        {
            if (!row.IsDeleted)
            {
                action(row);
            }
        }

        _ = CellEditedAsync();
    }

    private bool RequireEditable()
    {
        if (CanEdit)
        {
            return true;
        }

        Status = Server.ReadOnly ? $"\"{Server.Name}\" is read-only." : "Views are read-only here; edit the underlying table.";
        IsError = true;
        return false;
    }

    private void Watch(RowItem row) => row.PropertyChanged += OnRowChanged;

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RowItem.State))
        {
            UpdatePending();
        }
    }

    private void UpdatePending() => PendingCount = Rows.Count(r => r.State != RowState.Unchanged);

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (PendingCount == 0 || _context.Dialogs is null)
        {
            return true;
        }

        return await _context.Dialogs.ConfirmAsync("Pending Changes", $"Discard {PendingCount} pending change(s) and reload?");
    }

    private static string Summarize(IReadOnlyList<RowItem> rows, int column)
    {
        if (rows.Count < 2 || column < 0)
        {
            return string.Empty;
        }

        var values = rows.Select(r => r.CurrentValue(column)).ToList();
        var nonNull = values.Where(v => v is not null).ToList();
        var text = $"Count: {values.Count:N0}  Non-null: {nonNull.Count:N0}  Distinct: {values.Select(RowItem.Format).Distinct().Count():N0}";
        var numbers = nonNull
            .Select(v => decimal.TryParse(RowItem.Format(v), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null)
            .ToList();
        if (numbers.Count > 0 && numbers.All(n => n is not null))
        {
            var present = numbers.Select(n => n!.Value).ToList();
            text += $"  Sum: {present.Sum():G}  Avg: {present.Average():0.####}  Min: {present.Min():G}  Max: {present.Max():G}";
        }

        return text;
    }

    private async Task RunAsync(Func<DatabaseSession, Task> work)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await using var session = await _context.OpenSession();
            IsError = false;
            await work(session);
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
            IsError = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
            IsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One field of the record (transposed) view, editing the underlying row.</summary>
/// <param name="row">The row.</param>
/// <param name="index">The column position.</param>
/// <param name="name">The column name.</param>
/// <param name="type">The column type.</param>
public sealed class RecordField(RowItem row, int index, string name, string type)
{
    /// <summary>Gets the column name.</summary>
    public string Name => name;

    /// <summary>Gets the column type.</summary>
    public string Type => type;

    /// <summary>Gets or sets the value text.</summary>
    public string? Value
    {
        get => row[index];
        set => row[index] = value;
    }
}
