namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using Bengine.Introspection;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Compare;
using Bengine.Introspection.Model;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>One side of a data comparison: a table of a database of a server.</summary>
public sealed partial class TableEndpoint : ObservableObject
{
    private readonly ExplorerViewModel _explorer;
    private (string? Schema, string Name)? _wanted;

    [ObservableProperty] private ServerProfile? _server;
    [ObservableProperty] private string? _database;
    [ObservableProperty] private CatalogRelation? _table;

    /// <summary>Creates the endpoint.</summary>
    /// <param name="explorer">The explorer.</param>
    /// <param name="servers">The servers to choose from.</param>
    public TableEndpoint(ExplorerViewModel explorer, IReadOnlyList<ServerProfile> servers)
    {
        _explorer = explorer;
        Servers = servers;
    }

    /// <summary>Gets the servers.</summary>
    public IReadOnlyList<ServerProfile> Servers { get; }

    /// <summary>Gets the server's databases.</summary>
    public ObservableCollection<string> Databases { get; } = [];

    /// <summary>Gets the database's tables and views.</summary>
    public ObservableCollection<CatalogRelation> Tables { get; } = [];

    /// <summary>Gets a short label.</summary>
    public string Label => Table is { } t ? $"{Server?.Name} › {Database} › {(t.Schema is { } s ? s + "." : string.Empty)}{t.Name}" : "(choose a table)";

    /// <summary>Selects a table.</summary>
    /// <param name="server">The server.</param>
    /// <param name="database">The database.</param>
    /// <param name="schema">The table's schema.</param>
    /// <param name="name">The table's name.</param>
    /// <returns>A task that completes when the lists are loaded.</returns>
    public async Task SelectAsync(ServerProfile server, string? database, string? schema, string name)
    {
        _wanted = (schema, name);
        Server = server;
        await LoadDatabasesAsync();
        Database = database ?? Databases.FirstOrDefault();
        await LoadTablesAsync();
    }

    /// <summary>Opens a session on the database.</summary>
    /// <returns>The session.</returns>
    public Task<DatabaseSession> OpenAsync() => _explorer.OpenSessionAsync(Server!, Database);

    partial void OnServerChanged(ServerProfile? value)
    {
        OnPropertyChanged(nameof(Label));
        if (_wanted is null)
        {
            _ = LoadDatabasesAsync();
        }
    }

    partial void OnDatabaseChanged(string? value)
    {
        OnPropertyChanged(nameof(Label));
        if (_wanted is null)
        {
            _ = LoadTablesAsync();
        }
    }

    partial void OnTableChanged(CatalogRelation? value) => OnPropertyChanged(nameof(Label));

    private async Task LoadDatabasesAsync()
    {
        Databases.Clear();
        if (Server is null)
        {
            return;
        }

        try
        {
            foreach (var name in await _explorer.ListDatabasesAsync(Server))
            {
                Databases.Add(name);
            }
        }
        catch (IntrospectionException)
        {
            // Typed by hand instead.
        }

        if (Databases.Count == 0 && (Server.MaintenanceDatabase ?? (Server.Provider == Bengine.Enums.DatabaseProvider.Sqlite ? "main" : null)) is { } fallback)
        {
            Databases.Add(fallback);
        }
    }

    private async Task LoadTablesAsync()
    {
        var wanted = _wanted;
        _wanted = null;
        Tables.Clear();
        if (Server is null || Database is null)
        {
            return;
        }

        if (await _explorer.GetCatalogAsync(Server, Database) is { } catalog)
        {
            foreach (var relation in catalog.Relations.OrderBy(r => r.Schema).ThenBy(r => r.Name))
            {
                Tables.Add(relation);
            }
        }

        Table = wanted is { } w
            ? Tables.FirstOrDefault(t => t.Name == w.Name && (w.Schema is null || t.Schema is null || t.Schema == w.Schema))
            : Table is { } current ? Tables.FirstOrDefault(t => t.Name == current.Name) : null;
    }
}

/// <summary>One differing row as the grid shows it.</summary>
/// <param name="Marker">+ (only in source), − (only in target), ≠ (different).</param>
/// <param name="Kind">The difference.</param>
/// <param name="Cells">The cell texts; a changed cell reads <c>source → target</c>.</param>
/// <param name="Changed">Which cells changed.</param>
public sealed record DataDiffRow(string Marker, RowDiffKind Kind, IReadOnlyList<string> Cells, IReadOnlySet<int> Changed);

/// <summary>
/// The Data Compare tab: two tables (any servers, any providers) compared row
/// by row on their key, the differences listed with what changed, and the
/// INSERT/UPDATE/DELETE script that makes the target equal to the source —
/// shown, copied, or applied in one transaction.
/// </summary>
public sealed partial class DataCompareViewModel : DocumentViewModel
{
    private readonly IDialogService? _dialogs;
    private CancellationTokenSource? _cancel;

    [ObservableProperty] private string? _status = "Choose two tables and press Compare.";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _keyColumns = string.Empty;
    [ObservableProperty] private bool _syncInserts = true;
    [ObservableProperty] private bool _syncUpdates = true;
    [ObservableProperty] private bool _syncDeletes = true;
    [ObservableProperty] private string _script = string.Empty;
    [ObservableProperty] private DataCompareResult? _result;

    /// <summary>Creates the tab.</summary>
    /// <param name="explorer">The explorer.</param>
    /// <param name="servers">The servers.</param>
    public DataCompareViewModel(ExplorerViewModel explorer, IReadOnlyList<ServerProfile> servers)
    {
        _dialogs = explorer.Dialogs;
        Source = new TableEndpoint(explorer, servers);
        Target = new TableEndpoint(explorer, servers);
        Source.PropertyChanged += (_, e) => OnEndpointChanged(e.PropertyName);
        Target.PropertyChanged += (_, e) => OnEndpointChanged(e.PropertyName);
    }

    /// <summary>Raised when a new result needs new grid columns.</summary>
    public event Action? ResultChanged;

    /// <summary>Gets the source (the rows wanted).</summary>
    public TableEndpoint Source { get; }

    /// <summary>Gets the target (the rows to change).</summary>
    public TableEndpoint Target { get; }

    /// <inheritdoc />
    public override string Title => Source.Table is { } s && Target.Table is { } t ? $"Data: {s.Name} ↔ {t.Name}" : "Data Compare";

    /// <inheritdoc />
    public override string IconKind => "Table";

    /// <inheritdoc />
    public override string ToolTip => $"Data Compare — {Source.Label} → {Target.Label}";

    /// <summary>Gets the differing rows.</summary>
    public ObservableCollection<DataDiffRow> Rows { get; } = [];

    /// <summary>Gets a one-line summary of the result.</summary>
    public string Summary => Result is not { } r ? string.Empty
        : r.Identical ? $"Identical: {r.Same:N0} row(s) match on ({string.Join(", ", r.KeyColumns)})."
        : $"{r.Same:N0} equal · {r.Different:N0} different · {r.OnlyInSource:N0} only in source · {r.OnlyInTarget:N0} only in target"
          + (r.Truncated ? $" (first {r.Differences.Count:N0} shown)" : string.Empty)
          + (r.UnmatchedColumns.Count > 0 ? $" · not compared: {string.Join(", ", r.UnmatchedColumns)}" : string.Empty);

    /// <summary>Compares the two tables.</summary>
    /// <returns>A task that completes when compared.</returns>
    [RelayCommand]
    public async Task CompareAsync()
    {
        if (Source.Table is not { } sourceTable || Target.Table is not { } targetTable)
        {
            Fail("Choose a source and a target table.");
            return;
        }

        IsBusy = true;
        IsError = false;
        _cancel = new CancellationTokenSource();
        try
        {
            await using var source = await Source.OpenAsync();
            await using var target = await Target.OpenAsync();
            var keys = KeyColumns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var progress = new Progress<string>(line => Status = line);
            Result = await DataComparer.CompareAsync(source, sourceTable.Ref, target, targetTable.Ref, new DataCompareOptions { KeyColumns = keys.Length > 0 ? keys : null }, progress, _cancel.Token);
            Status = Summary;
        }
        catch (OperationCanceledException)
        {
            Fail("Cancelled.");
        }
        catch (IntrospectionException ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cancel = null;
        }
    }

    /// <summary>Applies the sync script to the target after review.</summary>
    /// <returns>A task that completes when applied and compared again.</returns>
    [RelayCommand]
    public async Task ApplyAsync()
    {
        if (Result is not { } result || Target.Table is not { } table || Target.Server is not { } server)
        {
            return;
        }

        if (server.ReadOnly)
        {
            Fail($"\"{server.Name}\" is read-only.");
            return;
        }

        var changes = Changes();
        if (changes.Count == 0 || (_dialogs is not null && !await _dialogs.ReviewSqlAsync($"Sync {table.Name} ({changes.Count} change(s))", Script, "Apply")))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await using var target = await Target.OpenAsync();
            var applied = await DataComparer.ApplyAsync(target, table.Ref, result, changes);
            IsBusy = false;
            await CompareAsync();
            Status = $"Applied {applied:N0} change(s). " + Summary;
        }
        catch (IntrospectionException ex)
        {
            Fail($"Nothing was changed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancel?.Cancel();

    [RelayCommand]
    private async Task SwapAsync()
    {
        var (server, database, table) = (Source.Server, Source.Database, Source.Table);
        if (Target.Server is { } targetServer && Target.Table is { } targetTable)
        {
            await Source.SelectAsync(targetServer, Target.Database, targetTable.Schema, targetTable.Name);
        }

        if (server is not null && table is not null)
        {
            await Target.SelectAsync(server, database, table.Schema, table.Name);
        }

        Result = null;
    }

    [RelayCommand]
    private Task CopyScriptAsync() => _dialogs?.SetClipboardAsync(Script) ?? Task.CompletedTask;

    partial void OnResultChanged(DataCompareResult? value)
    {
        Rows.Clear();
        if (value is not null)
        {
            foreach (var diff in value.Differences)
            {
                Rows.Add(ToRow(diff));
            }
        }

        UpdateScript();
        OnPropertyChanged(nameof(Summary));
        ResultChanged?.Invoke();
    }

    partial void OnSyncInsertsChanged(bool value) => UpdateScript();

    partial void OnSyncUpdatesChanged(bool value) => UpdateScript();

    partial void OnSyncDeletesChanged(bool value) => UpdateScript();

    private IReadOnlyList<RowChange> Changes() => Result?.SyncChanges(SyncInserts, SyncUpdates, SyncDeletes) ?? [];

    private void UpdateScript()
    {
        Script = Result is { } result && Target.Table is { } table && Target.Server is { } server
            ? result.Identical ? "-- The tables are identical." : DataComparer.SyncScript(server.Provider, table.Ref, result, Changes())
            : string.Empty;
    }

    private void OnEndpointChanged(string? property)
    {
        if (property == nameof(TableEndpoint.Label))
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    private static DataDiffRow ToRow(RowDiff diff)
    {
        static string Text(object? value) => RowItem.Format(value);

        var row = diff.Source ?? diff.Target!;
        var cells = new string[row.Count];
        for (var i = 0; i < row.Count; i++)
        {
            cells[i] = diff.Changed.Contains(i) ? $"{Text(diff.Source![i])} → {Text(diff.Target![i])}" : Text(row[i]);
        }

        var marker = diff.Kind switch { RowDiffKind.OnlyInSource => "+", RowDiffKind.OnlyInTarget => "−", _ => "≠" };
        return new DataDiffRow(marker, diff.Kind, cells, diff.Changed);
    }

    private void Fail(string message)
    {
        Status = message;
        IsError = true;
    }
}
