namespace Bengine.Introspection.Model;

/// <summary>What a staged row change does.</summary>
public enum RowChangeKind
{
    /// <summary>Inserts a new row.</summary>
    Insert,

    /// <summary>Updates the row identified by the key.</summary>
    Update,

    /// <summary>Deletes the row identified by the key.</summary>
    Delete,
}

/// <summary>
/// The SQL <c>DEFAULT</c> keyword as a cell value: an insert omits the column so
/// the server fills in its default, and an update renders <c>SET c = DEFAULT</c>.
/// </summary>
public sealed class SqlDefault
{
    private SqlDefault()
    {
    }

    /// <summary>Gets the single instance.</summary>
    public static SqlDefault Value { get; } = new();

    /// <inheritdoc />
    public override string ToString() => "DEFAULT";
}

/// <summary>
/// One pending edit of a data editor, applied with the others of its change set
/// in a single transaction. Updates and deletes identify their row by
/// <see cref="Key"/> (normally the primary key) and must affect exactly one row.
/// </summary>
public sealed record RowChange
{
    /// <summary>Gets what the change does.</summary>
    public required RowChangeKind Kind { get; init; }

    /// <summary>Gets the column/value pairs identifying the row (update, delete).</summary>
    public IReadOnlyDictionary<string, object?> Key { get; init; } = new Dictionary<string, object?>();

    /// <summary>Gets the column/value pairs to write (insert, update). A value may be <see cref="SqlDefault.Value"/>.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; init; } = new Dictionary<string, object?>();

    /// <summary>Creates an insert.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The change.</returns>
    public static RowChange Insert(IReadOnlyDictionary<string, object?> values) => new() { Kind = RowChangeKind.Insert, Values = values };

    /// <summary>Creates an update.</summary>
    /// <param name="key">The row key.</param>
    /// <param name="values">The new values.</param>
    /// <returns>The change.</returns>
    public static RowChange Update(IReadOnlyDictionary<string, object?> key, IReadOnlyDictionary<string, object?> values) =>
        new() { Kind = RowChangeKind.Update, Key = key, Values = values };

    /// <summary>Creates a delete.</summary>
    /// <param name="key">The row key.</param>
    /// <returns>The change.</returns>
    public static RowChange Delete(IReadOnlyDictionary<string, object?> key) => new() { Kind = RowChangeKind.Delete, Key = key };
}
