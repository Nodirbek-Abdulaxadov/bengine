namespace Bengine.Studio.ViewModels;

using System.ComponentModel;
using System.Globalization;
using Bengine.Introspection.Model;

/// <summary>How a cell is drawn.</summary>
[Flags]
public enum CellFlag
{
    /// <summary>An unchanged value.</summary>
    None = 0,

    /// <summary>The cell was edited and not yet submitted.</summary>
    Modified = 1,

    /// <summary>The cell is SQL <c>NULL</c>.</summary>
    Null = 2,

    /// <summary>The cell is the <c>DEFAULT</c> keyword.</summary>
    Default = 4,
}

/// <summary>A row's pending state in the data editor.</summary>
public enum RowState
{
    /// <summary>Loaded from the database and not edited.</summary>
    Unchanged,

    /// <summary>Loaded and edited.</summary>
    Modified,

    /// <summary>Added in the editor and not yet inserted.</summary>
    Inserted,

    /// <summary>Marked for deletion.</summary>
    Deleted,
}

/// <summary>
/// One data-grid row exposed to XAML as an integer-indexed, editable cell set.
/// Columns are built at runtime, one per result column, each bound to this
/// indexer by position (<c>[0]</c>, <c>[1]</c>, ...). The row keeps the original
/// values (to build the key of an update or delete) beside the edited text, and
/// tracks pending state for the editor's submit/revert model: edited cells,
/// SQL <c>NULL</c> (<c>&lt;null&gt;</c>) and <c>DEFAULT</c> cells, new rows, and
/// rows marked for deletion.
/// </summary>
public sealed class RowItem : INotifyPropertyChanged
{
    /// <summary>The text shown for SQL <c>NULL</c>; typing it sets a cell to <c>NULL</c>.</summary>
    public const string NullText = "<null>";

    /// <summary>The text shown for the <c>DEFAULT</c> keyword.</summary>
    public const string DefaultText = "<default>";

    private readonly object?[] _original;
    private readonly string?[] _display;
    private readonly string?[] _initial;
    private readonly CellFlag[] _flags;
    private readonly CellFlag[] _initialFlags;
    private bool _deleted;

    /// <summary>Creates a row from the reader's positional cell values and the column names.</summary>
    /// <param name="columns">The column names, aligned with <paramref name="values"/>.</param>
    /// <param name="values">The cell values in column order.</param>
    public RowItem(IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        Columns = columns;
        _original = [.. values];
        _display = values.Select(Format).ToArray();
        _flags = values.Select(v => v is null ? CellFlag.Null : CellFlag.None).ToArray();
        _initial = [.. _display];
        _initialFlags = [.. _flags];
        Flags = new CellFlags(this);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the column names aligned with the cells.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets a value indicating whether this is a new row not yet inserted.</summary>
    public bool IsNew { get; private init; }

    /// <summary>Gets a value indicating whether the row is marked for deletion.</summary>
    public bool IsDeleted => _deleted;

    /// <summary>Gets the row's pending state.</summary>
    public RowState State => IsNew ? RowState.Inserted : _deleted ? RowState.Deleted : HasChanges ? RowState.Modified : RowState.Unchanged;

    /// <summary>Gets whether any cell was edited.</summary>
    public bool HasChanges => Enumerable.Range(0, _display.Length).Any(IsChanged);

    /// <summary>Gets the per-cell drawing flags, bindable as <c>Flags[i]</c>.</summary>
    public CellFlags Flags { get; }

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

            if (value == NullText)
            {
                SetNull(index);
                return;
            }

            _display[index] = value;
            _flags[index] = CellFlag.None;
            Changed();
        }
    }

    /// <summary>Creates an empty, not-yet-inserted row whose cells are <c>DEFAULT</c>.</summary>
    /// <param name="columns">The column names.</param>
    /// <returns>A blank row flagged as new.</returns>
    public static RowItem Blank(IReadOnlyList<string> columns)
    {
        var row = new RowItem(columns, new object?[columns.Count]) { IsNew = true };
        for (var i = 0; i < columns.Count; i++)
        {
            row._display[i] = DefaultText;
            row._flags[i] = CellFlag.Default;
        }

        return row;
    }

    /// <summary>Creates a new row holding the current values of another (clone row).</summary>
    /// <param name="source">The row to copy.</param>
    /// <param name="skip">Columns to leave as <c>DEFAULT</c> (normally generated keys).</param>
    /// <returns>A new row.</returns>
    public static RowItem CloneOf(RowItem source, IEnumerable<string> skip)
    {
        var row = Blank(source.Columns);
        var skipped = new HashSet<string>(skip, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < source.Columns.Count; i++)
        {
            if (skipped.Contains(source.Columns[i]))
            {
                continue;
            }

            row._display[i] = source._display[i];
            row._flags[i] = source._flags[i] & (CellFlag.Null | CellFlag.Default);
        }

        return row;
    }

    /// <summary>Gets the original (pre-edit) value of a column, used to build a key predicate.</summary>
    /// <param name="column">The column name.</param>
    /// <returns>The original value, or <c>null</c> if the column is unknown.</returns>
    public object? OriginalValue(string column) => IndexOf(column) is var i and >= 0 ? _original[i] : null;

    /// <summary>Gets the current edited text of a column.</summary>
    /// <param name="column">The column name.</param>
    /// <returns>The edited text, or <c>null</c>.</returns>
    public string? EditedText(string column) => IndexOf(column) is var i and >= 0 ? _display[i] : null;

    /// <summary>Gets whether a column's text differs from what was loaded.</summary>
    /// <param name="column">The column name.</param>
    /// <returns><c>true</c> if the cell was edited.</returns>
    public bool IsChanged(string column) => IndexOf(column) is var i and >= 0 && IsChanged(i);

    /// <summary>Gets whether a cell differs from what was loaded.</summary>
    /// <param name="index">The column position.</param>
    /// <returns><c>true</c> if the cell was edited.</returns>
    public bool IsChanged(int index) => _display[index] != _initial[index] || _flags[index] != _initialFlags[index];

    /// <summary>Gets the value a cell would be written as: <c>null</c>, <see cref="SqlDefault.Value"/>, the edited text, or the loaded value.</summary>
    /// <param name="index">The column position.</param>
    /// <returns>The value.</returns>
    public object? CurrentValue(int index) =>
        _flags[index].HasFlag(CellFlag.Null) ? null
        : _flags[index].HasFlag(CellFlag.Default) ? SqlDefault.Value
        : IsChanged(index) || IsNew ? _display[index]
        : _original[index];

    /// <summary>Gets the current values of every cell.</summary>
    /// <returns>The values in column order.</returns>
    public IReadOnlyList<object?> CurrentValues() => Enumerable.Range(0, _display.Length).Select(CurrentValue).ToList();

    /// <summary>Sets a cell to SQL <c>NULL</c>.</summary>
    /// <param name="index">The column position.</param>
    public void SetNull(int index)
    {
        _display[index] = NullText;
        _flags[index] = CellFlag.Null;
        Changed();
    }

    /// <summary>Sets a cell to the <c>DEFAULT</c> keyword.</summary>
    /// <param name="index">The column position.</param>
    public void SetDefault(int index)
    {
        _display[index] = DefaultText;
        _flags[index] = CellFlag.Default;
        Changed();
    }

    /// <summary>Marks or unmarks the row for deletion.</summary>
    /// <param name="deleted">Whether the row is to be deleted.</param>
    public void MarkDeleted(bool deleted)
    {
        _deleted = deleted;
        Changed();
    }

    /// <summary>Discards every pending edit of the row.</summary>
    public void Revert()
    {
        Array.Copy(_initial, _display, _display.Length);
        Array.Copy(_initialFlags, _flags, _flags.Length);
        _deleted = false;
        Changed();
    }

    /// <summary>Gets the flag of a cell, including whether it is modified.</summary>
    /// <param name="index">The column position.</param>
    /// <returns>The flags.</returns>
    public CellFlag FlagOf(int index) =>
        index < 0 || index >= _flags.Length ? CellFlag.None : _flags[index] | (IsChanged(index) && !IsNew ? CellFlag.Modified : CellFlag.None);

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        Flags.Raise();
    }

    private int IndexOf(string column)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i], column, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (var i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i], column, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Formats a value for display the way the grid shows it.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    public static string Format(object? value) => value switch
    {
        null => NullText,
        byte[] bytes => "\\x" + Convert.ToHexString(bytes.Length > 64 ? bytes[..64] : bytes).ToLowerInvariant() + (bytes.Length > 64 ? "…" : string.Empty),
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>The per-cell flags of a row, bindable by index.</summary>
    public sealed class CellFlags(RowItem row) : INotifyPropertyChanged
    {
        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Gets the flags of a cell.</summary>
        /// <param name="index">The column position.</param>
        /// <returns>The flags.</returns>
        public CellFlag this[int index] => row.FlagOf(index);

        internal void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
    }
}
