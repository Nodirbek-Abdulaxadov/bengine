namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Export;
using Bengine.Introspection.Model;
using Bengine.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>
/// A "View/Edit Data" tab for one relation: paged reads with a filter and a
/// server-side sort, inline cell edits keyed by the primary key, adding and
/// deleting rows, and export. Every operation opens a short-lived session.
/// </summary>
public sealed partial class DataDocumentViewModel : DocumentViewModel
{
    private static readonly HashSet<DbDataType> NumericTypes =
    [
        DbDataType.TinyInt, DbDataType.SmallInt, DbDataType.Integer, DbDataType.BigInt,
        DbDataType.Decimal, DbDataType.Real, DbDataType.Double,
    ];

    private readonly Func<Task<DatabaseSession>> _open;
    private readonly IDialogService? _dialogs;
    private readonly DatabaseProvider _provider;
    private readonly Dictionary<string, DbDataType> _types = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _primaryKey = [];
    private SortSpec? _sort;

    [ObservableProperty] private string? _selectedFilterColumn;
    [ObservableProperty] private FilterOperator _selectedOperator = FilterOperator.Contains;
    [ObservableProperty] private string _filterValue = string.Empty;
    [ObservableProperty] private int _pageSize = 100;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private long? _totalCount;
    [ObservableProperty] private string _pageInfo = string.Empty;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private RowItem? _selectedRow;

    /// <summary>Creates the tab.</summary>
    /// <param name="open">Opens a session on the relation's database.</param>
    /// <param name="dialogs">The dialog service (for confirmations and save-as).</param>
    /// <param name="provider">The server's provider.</param>
    /// <param name="table">The relation.</param>
    /// <param name="location">A "server / database" label for the tooltip.</param>
    public DataDocumentViewModel(
        Func<Task<DatabaseSession>> open,
        IDialogService? dialogs,
        DatabaseProvider provider,
        TableRef table,
        string location)
    {
        _open = open;
        _dialogs = dialogs;
        _provider = provider;
        Table = table;
        Location = location;
    }

    /// <summary>Gets the relation shown in the tab.</summary>
    public TableRef Table { get; }

    /// <summary>Gets the "server / database" label.</summary>
    public string Location { get; }

    /// <inheritdoc />
    public override string Title => Table.Name;

    /// <inheritdoc />
    public override string IconKind => "Table";

    /// <inheritdoc />
    public override string ToolTip => $"View/Edit Data — {Location} / {Table.QualifiedName}";

    /// <summary>Gets the filter operators.</summary>
    public IReadOnlyList<FilterOperator> Operators { get; } = Enum.GetValues<FilterOperator>();

    /// <summary>Gets the page-size choices.</summary>
    public IReadOnlyList<int> PageSizes { get; } = [25, 50, 100, 250, 500, 1000];

    /// <summary>Gets the result column names.</summary>
    public ObservableCollection<string> Columns { get; } = [];

    /// <summary>Gets the rows of the current page (new, unsaved rows first).</summary>
    public ObservableCollection<RowItem> Rows { get; } = [];

    /// <summary>Raised when the column set changes: names, display types, and primary-key columns.</summary>
    public event Action<IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyList<string>>? ColumnsChanged;

    /// <summary>Loads the relation's structure and its first page.</summary>
    /// <returns>A task that completes when the page is shown.</returns>
    public Task InitializeAsync() => RunAsync(async session =>
    {
        var table = await session.Introspector.IntrospectTableAsync(Table);
        _primaryKey = table.PrimaryKey?.ColumnNames ?? [];
        foreach (var column in table.Columns)
        {
            _types[column.Name] = column.Type.DataType;
        }

        await LoadPageAsync(session);
    });

    [RelayCommand] private Task RefreshAsync() => RunAsync(LoadPageAsync);

    [RelayCommand]
    private Task NextPageAsync()
    {
        if (!HasMore) return Task.CompletedTask;
        Page++;
        return RunAsync(LoadPageAsync);
    }

    [RelayCommand]
    private Task PreviousPageAsync()
    {
        if (Page <= 1) return Task.CompletedTask;
        Page--;
        return RunAsync(LoadPageAsync);
    }

    [RelayCommand]
    private Task FirstPageAsync()
    {
        Page = 1;
        return RunAsync(LoadPageAsync);
    }

    [RelayCommand]
    private Task ApplyFilterAsync()
    {
        Page = 1;
        return RunAsync(LoadPageAsync);
    }

    [RelayCommand]
    private Task ClearFilterAsync()
    {
        SelectedFilterColumn = null;
        FilterValue = string.Empty;
        Page = 1;
        return RunAsync(LoadPageAsync);
    }

    [RelayCommand]
    private void AddRow()
    {
        Rows.Insert(0, RowItem.Blank(Columns.ToList()));
        Status = "New row added at the top — fill it in, then Save.";
        IsError = false;
    }

    [RelayCommand]
    private Task SaveNewRowsAsync() => RunAsync(async session =>
    {
        var pending = Rows.Where(r => r.IsNew).ToList();
        foreach (var row in pending)
        {
            var values = row.Columns
                .Select(c => (Column: c, Text: row.EditedText(c)))
                .Where(x => !string.IsNullOrEmpty(x.Text) && x.Text != "(null)")
                .ToDictionary(x => x.Column, x => Convert(x.Column, x.Text));
            await session.Writer.InsertAsync(Table, values);
        }

        Status = $"Inserted {pending.Count} row(s).";
        await LoadPageAsync(session);
    });

    [RelayCommand]
    private async Task DeleteRowAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        if (row.IsNew)
        {
            Rows.Remove(row);
            return;
        }

        if (_dialogs is not null && !await _dialogs.ConfirmAsync("Delete row", "Delete the selected row? This cannot be undone."))
        {
            return;
        }

        await RunAsync(async session =>
        {
            var affected = await session.Writer.DeleteAsync(Table, KeyOf(row));
            Status = $"Deleted {affected} row(s).";
            await LoadPageAsync(session);
        });
    }

    [RelayCommand] private Task ExportCsvAsync() => ExportAsync("csv");
    [RelayCommand] private Task ExportJsonAsync() => ExportAsync("json");
    [RelayCommand] private Task ExportSqlAsync() => ExportAsync("sql");

    /// <summary>Sorts by a column server-side: ascending, then descending, then unsorted.</summary>
    /// <param name="column">The column whose header was clicked.</param>
    /// <returns>A task that completes when the page is reloaded.</returns>
    public Task SortByAsync(string column)
    {
        _sort = _sort is null || !_sort.Column.Equals(column, StringComparison.OrdinalIgnoreCase)
            ? new SortSpec(column)
            : _sort.Direction == SortDirection.Ascending ? new SortSpec(column, SortDirection.Descending) : null;
        Page = 1;
        return RunAsync(LoadPageAsync);
    }

    /// <summary>Commits an inline cell edit (new rows wait for Save).</summary>
    /// <param name="row">The edited row.</param>
    /// <param name="column">The edited column.</param>
    /// <returns>A task that completes when the change is saved.</returns>
    public Task UpdateCellAsync(RowItem row, string column)
    {
        if (row.IsNew || !row.IsChanged(column))
        {
            return Task.CompletedTask;
        }

        return RunAsync(async session =>
        {
            var affected = await session.Writer.UpdateAsync(
                Table,
                KeyOf(row),
                new Dictionary<string, object?> { [column] = Convert(column, row.EditedText(column)) });
            Status = affected == 1 ? $"Updated {column}." : $"Update affected {affected} rows.";
            await LoadPageAsync(session);
        });
    }

    private async Task LoadPageAsync(DatabaseSession session)
    {
        var query = new DataQuery
        {
            Page = Page,
            PageSize = PageSize,
            IncludeTotalCount = true,
            Filters = BuildFilters(),
            Sort = _sort is null ? [] : [_sort],
        };

        var result = await session.Data.QueryAsync(Table, query);
        if (!Columns.SequenceEqual(result.Columns))
        {
            Columns.Clear();
            foreach (var column in result.Columns)
            {
                Columns.Add(column);
            }

            ColumnsChanged?.Invoke(result.Columns, result.ColumnTypes, _primaryKey);
        }

        Rows.Clear();
        foreach (var row in result.Rows)
        {
            Rows.Add(new RowItem(result.Columns, row));
        }

        TotalCount = result.TotalCount;
        HasMore = result.HasMore;
        var from = result.Rows.Count == 0 ? 0 : query.Offset + 1;
        var to = query.Offset + result.Rows.Count;
        var pages = TotalCount is { } total ? Math.Max(1, (int)Math.Ceiling(total / (double)PageSize)) : (int?)null;
        PageInfo = $"{from:N0}–{to:N0}" + (TotalCount is { } t ? $" of {t:N0}" : string.Empty) + $"  ·  page {Page}" + (pages is { } p ? $" / {p}" : string.Empty);
    }

    private async Task ExportAsync(string format)
    {
        var path = _dialogs is null ? null : await _dialogs.SaveFileAsync($"{Table.Name}.{format}");
        if (path is null)
        {
            return;
        }

        await RunAsync(async session =>
        {
            var page = await session.Data.QueryAsync(Table, new DataQuery
            {
                PageSize = DataQuery.MaxPageSize,
                Filters = BuildFilters(),
                Sort = _sort is null ? [] : [_sort],
            });
            var text = format switch
            {
                "json" => DataExporter.ToJson(page.Columns, page.Rows),
                "sql" => DataExporter.ToSqlInserts(_provider, Table, page.Columns, page.Rows),
                _ => DataExporter.ToCsv(page.Columns, page.Rows),
            };
            await File.WriteAllTextAsync(path, text);
            Status = $"Exported {page.Rows.Count:N0} row(s) to {path}";
        });
    }

    private IReadOnlyList<FilterCondition> BuildFilters()
    {
        if (string.IsNullOrWhiteSpace(SelectedFilterColumn))
        {
            return [];
        }

        var needsValue = SelectedOperator is not (FilterOperator.IsNull or FilterOperator.IsNotNull);
        return needsValue && string.IsNullOrEmpty(FilterValue)
            ? []
            : [new FilterCondition { Column = SelectedFilterColumn, Operator = SelectedOperator, Value = Convert(SelectedFilterColumn, FilterValue) }];
    }

    private Dictionary<string, object?> KeyOf(RowItem row)
    {
        var columns = _primaryKey.Count > 0 ? _primaryKey : row.Columns;
        return columns.ToDictionary(c => c, row.OriginalValue);
    }

    private object? Convert(string column, string? text)
    {
        if (string.IsNullOrEmpty(text) || text == "(null)")
        {
            return null;
        }

        return _types.TryGetValue(column, out var type) && NumericTypes.Contains(type)
            && decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)
            ? number
            : text;
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
            await using var session = await _open();
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
