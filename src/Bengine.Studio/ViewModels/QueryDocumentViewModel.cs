namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using Avalonia.Threading;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.History;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Sql;
using Bengine.Studio.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>The Query Tool's output tab: the log of statements and server messages.</summary>
public sealed class OutputTabViewModel
{
    /// <summary>Gets the tab title.</summary>
    public string Title => "Output";

    /// <summary>Gets the log lines.</summary>
    public ObservableCollection<MessageLine> Messages { get; } = [];
}

/// <summary>Whether the console commits each statement or waits for Commit.</summary>
public enum TransactionMode
{
    /// <summary>Each statement commits on its own (auto-commit).</summary>
    Auto,

    /// <summary>Statements run in an open transaction until Commit or Rollback.</summary>
    Manual,
}

/// <summary>What a Query Tool needs from the explorer for one server.</summary>
/// <param name="Server">The server.</param>
/// <param name="OpenSession">Opens a session on a database (asking for a password when needed).</param>
/// <param name="Catalog">Gets a database's metadata, from cache unless refresh is asked.</param>
/// <param name="Databases">Lists the server's databases.</param>
/// <param name="History">The query history.</param>
/// <param name="Dialogs">The dialogs.</param>
/// <param name="Files">The SQL file store (attachments and local history), or <c>null</c>.</param>
public sealed record QueryContext(
    ServerProfile Server,
    Func<string?, Task<DatabaseSession>> OpenSession,
    Func<string?, bool, Task<DatabaseCatalog?>> Catalog,
    Func<Task<IReadOnlyList<string>>> Databases,
    QueryHistory History,
    IDialogService? Dialogs,
    Bengine.Introspection.Projects.SqlFiles? Files = null);

/// <summary>
/// A Query Tool (console) bound to one database of a server. It keeps one open
/// session for its lifetime, so session settings and manual transactions carry
/// from statement to statement; runs the statement under the caret, the
/// selection, or the whole script; shows each result set in its own tab and
/// a log of every statement; asks for parameter values; refuses modifying
/// statements on read-only servers and confirms <c>UPDATE</c>/<c>DELETE</c>
/// without <c>WHERE</c>; and records everything in the query history. A tab
/// opened from a SQL file is bound to it: Ctrl+S saves it (recording a local
/// history version), and the file remembers the database it runs on.
/// </summary>
public sealed partial class QueryDocumentViewModel : DocumentViewModel, IAsyncDisposable
{
    private readonly QueryContext _context;
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _clock = new();
    private DatabaseSession? _session;
    private CancellationTokenSource? _cancel;
    private int _resultCounter;
    private string _savedText;

    [ObservableProperty] private string _queryText;
    [ObservableProperty] private int _maxRows = 1000;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _elapsed = string.Empty;
    [ObservableProperty] private object? _selectedResult;
    [ObservableProperty] private string? _database;
    [ObservableProperty] private TransactionMode _transactionMode;
    [ObservableProperty] private IsolationLevel _isolation = IsolationLevel.Unspecified;
    [ObservableProperty] private bool _inTransaction;
    [ObservableProperty] private int _pendingStatements;
    [ObservableProperty] private DatabaseCatalog? _catalog;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string? _filePath;

    /// <summary>Creates the tab.</summary>
    /// <param name="context">The server context.</param>
    /// <param name="database">The database, or <c>null</c> for the maintenance database.</param>
    /// <param name="sql">Initial editor text.</param>
    /// <param name="filePath">The SQL file the tab edits, or <c>null</c> for a console.</param>
    public QueryDocumentViewModel(QueryContext context, string? database, string? sql, string? filePath = null)
    {
        _context = context;
        _database = database;
        _queryText = sql ?? string.Empty;
        _savedText = _queryText;
        _filePath = filePath is null ? null : Path.GetFullPath(filePath);
        if (database is not null)
        {
            // The switcher must hold the selection before the full list arrives.
            Databases.Add(database);
        }

        _timer.Tick += (_, _) => Elapsed = FormatElapsed(_clock.Elapsed);
        Results.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasResults));
            RebuildTabs();
        };
        ResultTabs.Add(Output);
        _selectedResult = Output;
    }

    /// <summary>Gets the server.</summary>
    public ServerProfile Server => _context.Server;

    /// <summary>Gets the SQL dialect.</summary>
    public DatabaseProvider Provider => _context.Server.Provider;

    /// <summary>Gets the tab title: the file name (• when unsaved), or the console's database.</summary>
    public override string Title => FilePath is { } path
        ? Path.GetFileName(path) + (IsDirty ? " •" : string.Empty)
        : $"{Database ?? Server.MaintenanceDatabase ?? Server.Name}@{Server.Name}";

    /// <inheritdoc />
    public override string IconKind => "Function";

    /// <inheritdoc />
    public override string ToolTip => (FilePath is { } path ? path + "\n" : string.Empty)
        + $"Query Tool — {Server.Name} › {Database ?? "(default)"}" + (Server.ReadOnly ? " (read-only)" : string.Empty);

    /// <summary>Gets a value indicating whether the file has unsaved changes.</summary>
    public bool IsDirty => FilePath is not null && QueryText != _savedText;

    /// <summary>Gets a value indicating whether the tab edits a file.</summary>
    public bool HasFile => FilePath is not null;

    /// <inheritdoc />
    public override string? AccentColor => Server.Color;

    /// <summary>Gets a value indicating whether the server is read-only.</summary>
    public bool IsReadOnly => Server.ReadOnly;

    /// <summary>Gets whether the console offers manual transactions (MongoDB commands each run on their own).</summary>
    public bool SupportsTransactions => Provider != DatabaseProvider.MongoDb;

    /// <summary>Gets the row-limit choices.</summary>
    public IReadOnlyList<int> Limits { get; } = [100, 500, 1000, 5000, 10000, 50000];

    /// <summary>Gets the transaction modes.</summary>
    public IReadOnlyList<TransactionMode> TransactionModes { get; } = Enum.GetValues<TransactionMode>();

    /// <summary>Gets the isolation levels offered in manual mode.</summary>
    public IReadOnlyList<IsolationLevel> IsolationLevels { get; } =
        [IsolationLevel.Unspecified, IsolationLevel.ReadUncommitted, IsolationLevel.ReadCommitted, IsolationLevel.RepeatableRead, IsolationLevel.Serializable];

    /// <summary>Gets the server's databases for the switcher.</summary>
    public ObservableCollection<string> Databases { get; } = [];

    /// <summary>Gets the result tabs of the last run (and pinned earlier ones).</summary>
    public ObservableCollection<ResultSetViewModel> Results { get; } = [];

    /// <summary>Gets the plan of the last Explain, or <c>null</c>.</summary>
    public PlanResultViewModel? Plan { get; private set; }

    /// <summary>Gets the output log.</summary>
    public ObservableCollection<MessageLine> Messages => Output.Messages;

    /// <summary>Gets the output tab, always last among the result tabs.</summary>
    public OutputTabViewModel Output { get; } = new();

    /// <summary>Gets the result tabs followed by the output tab.</summary>
    public ObservableCollection<object> ResultTabs { get; } = [];

    /// <summary>Gets a value indicating whether there is at least one result tab.</summary>
    public bool HasResults => Results.Count > 0;

    /// <summary>Raised with editor markers to show (an error position), or an empty list to clear them.</summary>
    public event Action<IReadOnlyList<EditorMarker>>? MarkersChanged;

    /// <summary>Raised when the editor text should be replaced (history, formatting from outside).</summary>
    public event Action<string>? InsertRequested;

    /// <summary>Raised when the editor should move the caret to a line.</summary>
    public event Action<int>? LineRequested;

    /// <summary>Gets the line the editor should show once it is attached, or <c>null</c>.</summary>
    public int? PendingLine { get; private set; }

    /// <summary>Moves the editor's caret to a line (now, or when the tab is first shown).</summary>
    /// <param name="line">The 1-based line.</param>
    public void GoToLine(int line)
    {
        PendingLine = line;
        LineRequested?.Invoke(line);
    }

    /// <summary>Marks the pending line as shown.</summary>
    public void ClearPendingLine() => PendingLine = null;

    private void RebuildTabs()
    {
        ResultTabs.Clear();
        if (Plan is not null)
        {
            ResultTabs.Add(Plan);
        }

        foreach (var result in Results)
        {
            ResultTabs.Add(result);
        }

        ResultTabs.Add(Output);
    }

    /// <summary>Loads the database list and the catalog for completion.</summary>
    /// <returns>A task that completes when loaded.</returns>
    public async Task InitializeAsync()
    {
        try
        {
            foreach (var name in await _context.Databases())
            {
                if (!Databases.Contains(name))
                {
                    Databases.Add(name);
                }
            }
        }
        catch (IntrospectionException)
        {
            // The switcher stays empty; the console still works on its database.
        }


        await LoadCatalogAsync(refresh: false);
    }

    /// <summary>Runs SQL: each statement in turn, stopping at the first failure.</summary>
    /// <param name="sql">The text to run (a selection, a statement, or the whole script).</param>
    /// <param name="offset">The text's offset in the editor, to place error markers.</param>
    /// <returns>A task that completes when the run ends.</returns>
    public async Task ExecuteAsync(string sql, int offset = 0)
    {
        var statements = SqlScript.Split(sql, Provider);
        if (statements.Count == 0 || IsBusy)
        {
            return;
        }

        await RunAsync(statements.Select(s => (s.Text, offset + s.Start)).ToList());
    }

    /// <summary>Shows the execution plan of a statement in a Plan tab.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="offset">Its offset in the editor.</param>
    /// <param name="analyze">Whether to execute it and report actual rows and times.</param>
    /// <returns>A task that completes when the plan is shown.</returns>
    public async Task ExplainAsync(string sql, int offset, bool analyze)
    {
        var statement = SqlScript.Split(sql, Provider).FirstOrDefault();
        if (statement is null || IsBusy)
        {
            return;
        }

        var text = statement.Text;
        var info = SqlStatementInfo.Classify(text, Provider);
        if (analyze && !await AllowedAsync(info, text))
        {
            return;
        }

        IsBusy = true;
        IsError = false;
        MarkersChanged?.Invoke([]);
        _cancel = new CancellationTokenSource();
        _clock.Restart();
        _timer.Start();
        try
        {
            var session = await EnsureSessionAsync();
            Log((analyze ? "EXPLAIN ANALYZE " : "EXPLAIN ") + OneLine(text), MessageKind.Statement);
            var plan = await Bengine.Introspection.Plans.PlanReader.ExplainAsync(session!, text, analyze, _cancel.Token);
            Plan = new PlanResultViewModel(plan, text);
            RebuildTabs();
            SelectedResult = Plan;
            Status = Plan.Summary;
            Log(Plan.Summary, MessageKind.Success);
            if (analyze && info.CanModify)
            {
                Log("The analyzed statement was rolled back.", MessageKind.Info);
            }
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
            IsError = true;
            Log(ex.Message, MessageKind.Error);
            if (ex.Position is { } position)
            {
                MarkersChanged?.Invoke([new EditorMarker(offset + statement.Start + Math.Max(0, position - 1), 1, ex.Message)]);
            }

            SelectedResult = Output;
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        finally
        {
            _timer.Stop();
            Elapsed = FormatElapsed(_clock.Elapsed);
            IsBusy = false;
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cancel is { IsCancellationRequested: false } cancel)
        {
            cancel.Cancel();
            Log("Cancelling…", MessageKind.Info);
        }
    }

    [RelayCommand]
    private Task CommitAsync() => EndTransactionAsync(commit: true);

    [RelayCommand]
    private Task RollbackAsync() => EndTransactionAsync(commit: false);

    [RelayCommand]
    private async Task ShowHistoryAsync()
    {
        if (_context.Dialogs is { } dialogs && await dialogs.ShowHistoryAsync(_context.History, Server.Id) is { } entry)
        {
            InsertRequested?.Invoke(entry.Sql);
        }
    }

    [RelayCommand]
    private Task RefreshCatalogAsync() => LoadCatalogAsync(refresh: true);

    [RelayCommand]
    private void ClearOutput()
    {
        Messages.Clear();
        for (var i = Results.Count - 1; i >= 0; i--)
        {
            if (!Results[i].IsPinned)
            {
                Results.RemoveAt(i);
            }
        }
    }

    /// <summary>Saves the editor text to its file, asking for one when the tab is a console.</summary>
    /// <returns><c>true</c> when saved.</returns>
    public async Task<bool> SaveAsync()
    {
        if (FilePath is null)
        {
            return await SaveAsAsync();
        }

        try
        {
            var text = QueryText;
            (_context.Files ?? new Bengine.Introspection.Projects.SqlFiles()).Save(FilePath, text);
            _savedText = text;
            Attach();
            OnFileStateChanged();
            Status = $"Saved {Path.GetFileName(FilePath)}.";
            IsError = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save: {ex.Message}";
            IsError = true;
            return false;
        }
    }

    /// <summary>Saves the editor text to a new file and binds the tab to it.</summary>
    /// <returns><c>true</c> when saved.</returns>
    [RelayCommand]
    public async Task<bool> SaveAsAsync()
    {
        if (_context.Dialogs is not { } dialogs
            || await dialogs.SaveFileAsync(FilePath is { } current ? Path.GetFileName(current) : $"{Database ?? Server.Name}.sql") is not { } path)
        {
            return false;
        }

        FilePath = Path.GetFullPath(path);
        return await SaveAsync();
    }

    /// <summary>Shows the file's local history and restores a chosen version into the editor.</summary>
    /// <returns>A task that completes when the dialog closes.</returns>
    [RelayCommand]
    public async Task ShowLocalHistoryAsync()
    {
        if (FilePath is null || _context.Dialogs is not { } dialogs)
        {
            Status = "Local history is kept for saved SQL files — save the console first (Ctrl+S).";
            return;
        }

        var files = _context.Files ?? new Bengine.Introspection.Projects.SqlFiles();
        if (await dialogs.ShowLocalHistoryAsync($"Local History — {Path.GetFileName(FilePath)}", files.History(FilePath), QueryText) is { } restored)
        {
            QueryText = restored;
            Log($"Restored a version of {Path.GetFileName(FilePath)} from local history (not saved yet).", MessageKind.Info);
        }
    }

    [RelayCommand]
    private Task SaveFileAsync() => SaveAsync();

    partial void OnQueryTextChanged(string value) => OnFileStateChanged();

    partial void OnFilePathChanged(string? value) => OnFileStateChanged();

    private void OnFileStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTip));
    }

    // The file remembers where it last ran.
    private void Attach()
    {
        if (FilePath is not null && _context.Files is { } files)
        {
            try
            {
                files.Attach(FilePath, new Bengine.Introspection.Projects.FileTarget(Server.Id, Database));
            }
            catch (IOException)
            {
                // The association is a convenience.
            }
        }
    }

    /// <summary>Closes the session, asking first when a transaction is open or the file is unsaved.</summary>
    /// <returns><c>true</c> when the tab may close.</returns>
    public async Task<bool> CanCloseAsync()
    {
        if (IsDirty && _context.Dialogs is { } ask
            && await ask.ConfirmAsync("Unsaved Changes", $"Save the changes to {Path.GetFileName(FilePath)} before closing? (No discards them.)"))
        {
            await SaveAsync();
        }

        if (_session is { InTransaction: true } && _context.Dialogs is { } dialogs)
        {
            var commit = await dialogs.ConfirmAsync(
                "Open Transaction",
                $"The console has an open transaction with {PendingStatements} statement(s). Commit it before closing? (No rolls it back.)");
            await EndTransactionAsync(commit);
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        _cancel?.Cancel();
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }

    partial void OnDatabaseChanged(string? oldValue, string? newValue)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTip));
        Attach();
        if (_session is null || oldValue == newValue)
        {
            _ = LoadCatalogAsync(refresh: false);
            return;
        }

        if (_session.InTransaction)
        {
            Log("Finish the open transaction (Commit or Rollback) before switching databases.", MessageKind.Error);
            Dispatcher.UIThread.Post(() => Database = oldValue);
            return;
        }

        _ = SwitchDatabaseAsync();
    }

    partial void OnTransactionModeChanged(TransactionMode value)
    {
        if (value == TransactionMode.Auto && _session is { InTransaction: true })
        {
            Log("Switching to auto-commit: commit or roll back the open transaction first.", MessageKind.Info);
        }
    }

    private async Task SwitchDatabaseAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
            IsConnected = false;
        }

        Log($"Switched to database {Database}.", MessageKind.Info);
        await LoadCatalogAsync(refresh: false);
    }

    private async Task LoadCatalogAsync(bool refresh)
    {
        try
        {
            Catalog = await _context.Catalog(Database, refresh) ?? Catalog;
        }
        catch (IntrospectionException ex)
        {
            Log($"Could not load metadata for completion: {ex.Message}", MessageKind.Info);
        }
    }

    private async Task<DatabaseSession?> EnsureSessionAsync()
    {
        if (_session is { IsOpen: true })
        {
            return _session;
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
            InTransaction = false;
            Log("The connection was lost; reconnecting.", MessageKind.Info);
        }

        _session = await _context.OpenSession(Database);
        _session.ServerMessage += message => Dispatcher.UIThread.Post(() => Log(message, MessageKind.Info));
        IsConnected = true;
        return _session;
    }

    private async Task RunAsync(IReadOnlyList<(string Sql, int Offset)> statements)
    {
        if (FilePath is not null && _context.Files is { } files)
        {
            try
            {
                // Local history keeps what ran, saved or not.
                files.Snapshot(FilePath, QueryText);
            }
            catch (IOException)
            {
                // History is best effort.
            }
        }

        IsBusy = true;
        IsError = false;
        MarkersChanged?.Invoke([]);
        _cancel = new CancellationTokenSource();
        _clock.Restart();
        _timer.Start();
        Status = "Running…";
        var keepResults = Results.Where(r => r.IsPinned).ToList();
        var fresh = new List<ResultSetViewModel>();
        var catalogStale = false;
        var ran = 0;

        try
        {
            var session = await EnsureSessionAsync();
            if (session is null)
            {
                return;
            }

            foreach (var (original, offset) in statements)
            {
                var sql = original;
                if (_cancel.IsCancellationRequested)
                {
                    break;
                }

                // Parameters.
                var names = SqlParameters.Names(sql, Provider);
                if (names.Count > 0)
                {
                    if (_context.Dialogs is null || await _context.Dialogs.AskParametersAsync(names, _parameters) is not { } values)
                    {
                        Log("Cancelled: parameter values were not given.", MessageKind.Info);
                        break;
                    }

                    foreach (var (name, value) in values)
                    {
                        _parameters[name] = value;
                    }

                    sql = SqlParameters.Substitute(sql, values, Provider);
                }

                var info = SqlStatementInfo.Classify(sql, Provider);
                if (!await AllowedAsync(info, sql))
                {
                    break;
                }

                // A typed COMMIT / ROLLBACK ends the console's own transaction.
                if (session.InTransaction && info.Kind == SqlStatementKind.Transaction && info.Verb is "COMMIT" or "ROLLBACK" or "END" or "ABORT")
                {
                    await EndTransactionAsync(info.Verb == "COMMIT" || info.Verb == "END");
                    ran++;
                    continue;
                }

                if (TransactionMode == TransactionMode.Manual && !session.InTransaction && info.Kind != SqlStatementKind.Transaction)
                {
                    await session.BeginTransactionAsync(Isolation, _cancel.Token);
                    InTransaction = true;
                    PendingStatements = 0;
                    Log($"Transaction started{(Isolation == IsolationLevel.Unspecified ? string.Empty : $" ({Isolation})")}.", MessageKind.Info);
                }

                Log(OneLine(sql), MessageKind.Statement);
                var started = Stopwatch.StartNew();
                try
                {
                    var results = await session.Query.ExecuteAllAsync(sql, MaxRows, _cancel.Token);
                    started.Stop();
                    ran++;
                    if (session.InTransaction)
                    {
                        PendingStatements++;
                    }

                    long? rows = null;
                    foreach (var result in results)
                    {
                        if (result.IsQuery)
                        {
                            var title = $"Result {++_resultCounter}";
                            fresh.Add(new ResultSetViewModel(title, sql, result));
                            rows = (rows ?? 0) + result.Rows.Count;
                            Log($"{result.Rows.Count:N0} row(s) retrieved{(result.Truncated ? $" (limited to {MaxRows:N0})" : string.Empty)} in {started.Elapsed.TotalMilliseconds:N0} ms", MessageKind.Success);
                        }
                        else
                        {
                            rows = result.RowsAffected;
                            Log(result.RowsAffected >= 0
                                ? $"{result.RowsAffected:N0} row(s) affected in {started.Elapsed.TotalMilliseconds:N0} ms"
                                : $"Completed in {started.Elapsed.TotalMilliseconds:N0} ms", MessageKind.Success);
                        }
                    }

                    catalogStale |= info.Kind == SqlStatementKind.Ddl;
                    Record(sql, started.Elapsed.TotalMilliseconds, rows, null);
                }
                catch (IntrospectionException ex)
                {
                    started.Stop();
                    Record(sql, started.Elapsed.TotalMilliseconds, null, ex.Message);
                    Log(ex.Message, MessageKind.Error);
                    Status = ex.Message;
                    IsError = true;
                    if (ex.Position is { } position)
                    {
                        var at = offset + Math.Max(0, position - 1);
                        MarkersChanged?.Invoke([new EditorMarker(at, WordLength(sql, position - 1), ex.Message)]);
                    }
                    else
                    {
                        MarkersChanged?.Invoke([new EditorMarker(offset, Math.Min(sql.Length, sql.IndexOf('\n') is var nl and > 0 ? nl : sql.Length), ex.Message)]);
                    }

                    if (session.InTransaction && Provider == DatabaseProvider.PostgreSql)
                    {
                        Log("PostgreSQL aborted the transaction; roll it back to continue.", MessageKind.Info);
                    }

                    break;
                }
            }

            if (!IsError)
            {
                Status = _cancel.IsCancellationRequested
                    ? "Cancelled."
                    : $"{ran} statement(s) executed in {FormatElapsed(_clock.Elapsed)}" + (session.InTransaction ? " · transaction open" : string.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
            Log("Statement cancelled.", MessageKind.Error);
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
            IsError = true;
            Log(ex.Message, MessageKind.Error);
        }
        finally
        {
            _timer.Stop();
            Elapsed = FormatElapsed(_clock.Elapsed);
            InTransaction = _session?.InTransaction ?? false;
            if (fresh.Count > 0)
            {
                Results.Clear();
                foreach (var result in keepResults.Concat(fresh))
                {
                    Results.Add(result);
                }

                SelectedResult = IsError ? Output : fresh[^1];
            }
            else if (statements.Count > 0)
            {
                SelectedResult = Output;
            }

            IsBusy = false;
            _cancel?.Dispose();
            _cancel = null;
        }

        if (catalogStale)
        {
            await LoadCatalogAsync(refresh: true);
        }
    }

    private async Task<bool> AllowedAsync(SqlStatementInfo info, string sql)
    {
        if (Server.ReadOnly && info.CanModify)
        {
            var message = $"\"{Server.Name}\" is read-only: {info.Verb} statements are not run. Clear Read-only in the server properties to allow changes.";
            Log(message, MessageKind.Error);
            Status = message;
            IsError = true;
            return false;
        }

        var unfiltered = Provider == DatabaseProvider.MongoDb
            ? $"This {info.Verb} has an empty filter and affects every document of the collection"
            : $"This {info.Verb} has no WHERE clause and affects every row of the table";
        if (info.IsUnfilteredDml && _context.Dialogs is { } dialogs
            && !await dialogs.ConfirmAsync("Unsafe Statement", $"{unfiltered}:\n\n{OneLine(sql)}\n\nRun it anyway?"))
        {
            Log(Provider == DatabaseProvider.MongoDb ? $"Not run: {info.Verb} with an empty filter." : $"Not run: {info.Verb} without WHERE.", MessageKind.Info);
            return false;
        }

        return true;
    }

    private async Task EndTransactionAsync(bool commit)
    {
        if (_session is not { InTransaction: true } session)
        {
            Log("No transaction is open.", MessageKind.Info);
            return;
        }

        try
        {
            if (commit)
            {
                await session.CommitAsync();
            }
            else
            {
                await session.RollbackAsync();
            }

            Log(commit ? $"Committed {PendingStatements} statement(s)." : $"Rolled back {PendingStatements} statement(s).", MessageKind.Success);
            Status = commit ? "Transaction committed." : "Transaction rolled back.";
            IsError = false;
        }
        catch (IntrospectionException ex)
        {
            Log(ex.Message, MessageKind.Error);
            Status = ex.Message;
            IsError = true;
        }
        finally
        {
            InTransaction = session.InTransaction;
            PendingStatements = 0;
        }

        if (commit)
        {
            await LoadCatalogAsync(refresh: true);
        }
    }

    private void Record(string sql, double ms, long? rows, string? error)
    {
        try
        {
            _context.History.Add(new QueryHistoryEntry
            {
                Time = DateTimeOffset.Now,
                ServerId = Server.Id,
                Database = Database,
                Sql = sql,
                DurationMs = Math.Round(ms, 1),
                Rows = rows,
                Error = error,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; a full disk must not fail the query.
        }
    }

    private void Log(string text, MessageKind kind) => Messages.Add(new MessageLine(DateTime.Now, text, kind));

    private static string OneLine(string sql)
    {
        var flat = string.Join(' ', sql.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())).Trim();
        return flat.Length > 300 ? flat[..300] + "…" : flat;
    }

    private static int WordLength(string sql, int index)
    {
        var end = Math.Clamp(index, 0, sql.Length);
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] is '_' or '"' or '.'))
        {
            end++;
        }

        return Math.Max(1, end - index);
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 1 ? $"{elapsed.TotalMilliseconds:N0} ms"
        : elapsed.TotalMinutes < 1 ? $"{elapsed.TotalSeconds:N1} s"
        : $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s";
}
