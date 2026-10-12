namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using Bengine.Introspection;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// A Query Tool tab bound to one database: runs the selected text (or the whole
/// editor), shows the result grid, and keeps a message log.
/// </summary>
public sealed partial class QueryDocumentViewModel : DocumentViewModel
{
    private readonly Func<Task<DatabaseSession>> _open;

    [ObservableProperty] private string _queryText;
    [ObservableProperty] private int _maxRows = 1000;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _resultTabIndex;

    /// <summary>Creates the tab.</summary>
    /// <param name="open">Opens a session on the target database.</param>
    /// <param name="title">The tab title (<c>db/user@server</c>).</param>
    /// <param name="location">A "server › database" label.</param>
    /// <param name="sql">Initial editor text.</param>
    public QueryDocumentViewModel(Func<Task<DatabaseSession>> open, string title, string location, string? sql)
    {
        _open = open;
        TabTitle = title;
        Location = location;
        _queryText = sql ?? string.Empty;
    }

    /// <summary>Gets the tab title.</summary>
    public string TabTitle { get; }

    /// <summary>Gets the "server › database" label.</summary>
    public string Location { get; }

    /// <inheritdoc />
    public override string Title => TabTitle;

    /// <inheritdoc />
    public override string IconKind => "Function";

    /// <inheritdoc />
    public override string ToolTip => $"Query Tool — {Location}";

    /// <summary>Gets the row-limit choices.</summary>
    public IReadOnlyList<int> Limits { get; } = [100, 500, 1000, 5000, 10000];

    /// <summary>Gets the result rows.</summary>
    public ObservableCollection<RowItem> Rows { get; } = [];

    /// <summary>Gets the message log, newest first.</summary>
    public ObservableCollection<string> Messages { get; } = [];

    /// <summary>Raised when a result set arrives: column names and type names.</summary>
    public event Action<IReadOnlyList<string>, IReadOnlyList<string>>? ColumnsChanged;

    /// <summary>Runs the given text, or the whole editor when it is empty.</summary>
    /// <param name="selection">The selected text, or <c>null</c>.</param>
    /// <param name="explain">Whether to prefix the statement with <c>EXPLAIN</c>.</param>
    /// <returns>A task that completes when the result is shown.</returns>
    public async Task ExecuteAsync(string? selection, bool explain = false)
    {
        var sql = (string.IsNullOrWhiteSpace(selection) ? QueryText : selection).Trim();
        if (sql.Length == 0 || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = "Running…";
        IsError = false;
        try
        {
            await using var session = await _open();
            var result = await session.Query.ExecuteAsync(explain ? $"EXPLAIN {sql}" : sql, MaxRows);
            Rows.Clear();
            if (result.IsQuery)
            {
                ColumnsChanged?.Invoke(result.Columns, result.ColumnTypes);
                foreach (var row in result.Rows)
                {
                    Rows.Add(new RowItem(result.Columns, row));
                }

                Status = $"Successfully run. Total query runtime: {result.ElapsedMilliseconds:F1} msec. {result.Rows.Count:N0} rows" +
                         (result.Truncated ? $" (limited to {MaxRows:N0})." : ".");
                ResultTabIndex = 0;
            }
            else
            {
                ColumnsChanged?.Invoke([], []);
                Status = $"Query returned successfully: {result.RowsAffected:N0} rows affected, {result.ElapsedMilliseconds:F1} msec.";
                ResultTabIndex = 1;
            }
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
            IsError = true;
            ResultTabIndex = 1;
        }
        finally
        {
            Messages.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {Status}");
            IsBusy = false;
        }
    }
}
