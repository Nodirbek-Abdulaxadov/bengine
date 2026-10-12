namespace Bengine.Studio.ViewModels;

using System.ComponentModel;
using System.Globalization;

/// <summary>
/// One data-grid row exposed to XAML as an integer-indexed, editable cell set.
/// Columns are built at runtime, one per result column, each bound to this
/// indexer by position (<c>[0]</c>, <c>[1]</c>, ...). The row keeps the original
/// values (to build the primary-key predicate of an update) alongside the edited
/// text. Database <c>NULL</c> shows as <c>(null)</c>.
/// </summary>
public sealed class RowItem : INotifyPropertyChanged
{
    private readonly object?[] _original;
    private readonly string?[] _display;
    private readonly string?[] _initial;

    /// <summary>Creates a row from the reader's positional cell values and the column names.</summary>
    /// <param name="columns">The column names, aligned with <paramref name="values"/>.</param>
    /// <param name="values">The cell values in column order.</param>
    public RowItem(IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        Columns = columns;
        _original = [.. values];
        _display = values.Select(Format).ToArray();
        _initial = [.. _display];
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the column names aligned with the cells.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets a value indicating whether this is a new row not yet inserted.</summary>
    public bool IsNew { get; private init; }

    /// <summary>Gets or sets the display/edit text of the cell at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based column position.</param>
    /// <returns>The cell's text.</returns>
    public string? this[int index]
    {
        get => index < 0 || index >= _display.Length ? null : _display[index];
        set
        {
            if (index < 0 || index >= _display.Length || _display[index] == value)
            {
                return;
            }

            _display[index] = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    /// <summary>Creates an empty, not-yet-inserted row.</summary>
    /// <param name="columns">The column names.</param>
    /// <returns>A blank row flagged as new.</returns>
    public static RowItem Blank(IReadOnlyList<string> columns) =>
        new RowItem(columns, new object?[columns.Count]) { IsNew = true }.Cleared();

    /// <summary>Gets the original (pre-edit) value of a column, used to build a key predicate.</summary>
    /// <param name="column">The column name.</param>
    /// <returns>The original value, or <c>null</c> if the column is unknown.</returns>
    public object? OriginalValue(string column) => IndexOf(column) is { } i and >= 0 ? _original[i] : null;

    /// <summary>Gets the current edited text of a column.</summary>
    /// <param name="column">The column name.</param>
    /// <returns>The edited text, or <c>null</c>.</returns>
    public string? EditedText(string column) => IndexOf(column) is { } i and >= 0 ? _display[i] : null;

    /// <summary>Gets whether a column's text differs from what was loaded.</summary>
    /// <param name="column">The column name.</param>
    /// <returns><c>true</c> if the cell was edited.</returns>
    public bool IsChanged(string column) => IndexOf(column) is { } i and >= 0 && _display[i] != _initial[i];

    private int IndexOf(string column)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i], column, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private RowItem Cleared()
    {
        Array.Fill(_display, string.Empty);
        Array.Fill(_initial, string.Empty);
        return this;
    }

    private static string? Format(object? value) => value switch
    {
        null => "(null)",
        byte[] bytes => "\\x" + Convert.ToHexString(bytes.Length > 64 ? bytes[..64] : bytes).ToLowerInvariant() + (bytes.Length > 64 ? "…" : string.Empty),
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
