namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using Bengine.Introspection.Model;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// One result set of a Query Tool run, shown in its own result tab: the
/// columns with their database types, the rows, and the statement that
/// produced it. A pinned result survives the next run.
/// </summary>
public sealed partial class ResultSetViewModel : ObservableObject
{
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string _title;

    /// <summary>Creates a result tab from a query result.</summary>
    /// <param name="title">The tab title.</param>
    /// <param name="sql">The statement that produced it.</param>
    /// <param name="result">The result.</param>
    public ResultSetViewModel(string title, string sql, QueryResult result)
    {
        _title = title;
        Sql = sql;
        Columns = result.Columns;
        ColumnTypes = result.ColumnTypes;
        Truncated = result.Truncated;
        ElapsedMilliseconds = result.ElapsedMilliseconds;
        foreach (var row in result.Rows)
        {
            Rows.Add(new RowItem(result.Columns, row));
        }
    }

    /// <summary>Gets the statement that produced the result.</summary>
    public string Sql { get; }

    /// <summary>Gets the column names.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets the database type name of each column.</summary>
    public IReadOnlyList<string> ColumnTypes { get; }

    /// <summary>Gets the rows.</summary>
    public ObservableCollection<RowItem> Rows { get; } = [];

    /// <summary>Gets a value indicating whether the row cap cut the result short.</summary>
    public bool Truncated { get; }

    /// <summary>Gets the execution time.</summary>
    public double ElapsedMilliseconds { get; }

    /// <summary>Gets the summary shown under the grid.</summary>
    public string Summary => $"{Rows.Count:N0} row{(Rows.Count == 1 ? string.Empty : "s")}{(Truncated ? " (limit reached)" : string.Empty)} · {ElapsedMilliseconds:N0} ms";

    /// <summary>Gets the raw cell values of the rows, for export and copy.</summary>
    /// <param name="rows">The rows to take, or <c>null</c> for all.</param>
    /// <returns>The values.</returns>
    public IReadOnlyList<IReadOnlyList<object?>> Values(IEnumerable<RowItem>? rows = null) =>
        (rows ?? Rows).Select(r => r.CurrentValues()).ToList();
}

/// <summary>One line of the Query Tool's output log.</summary>
/// <param name="Time">When it was logged.</param>
/// <param name="Text">The message.</param>
/// <param name="Kind">How it is colored.</param>
public sealed record MessageLine(DateTime Time, string Text, MessageKind Kind)
{
    /// <summary>Gets the time as shown.</summary>
    public string Stamp => Time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The kind of an output line.</summary>
public enum MessageKind
{
    /// <summary>A statement as it was run.</summary>
    Statement,

    /// <summary>A success.</summary>
    Success,

    /// <summary>A failure.</summary>
    Error,

    /// <summary>Server output (notices, PRINT) or neutral information.</summary>
    Info,
}
