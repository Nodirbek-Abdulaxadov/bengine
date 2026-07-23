namespace Bengine.Models;

using Bengine.Enums;

/// <summary>
/// Immutable reference to a table column participating in an index, together
/// with its ordering within the key. References the column by name so the index
/// never holds a back-reference to the <see cref="Column"/> instance.
/// </summary>
public sealed record IndexColumn
{
    /// <summary>Gets the name of the referenced column.</summary>
    public required string ColumnName { get; init; }

    /// <summary>Gets the sort direction of this key column.</summary>
    public SortDirection Direction { get; init; } = SortDirection.Ascending;

    /// <summary>Gets the placement of <c>NULL</c> values within the ordering.</summary>
    public NullsOrder Nulls { get; init; } = NullsOrder.Default;

    /// <summary>Creates an ascending key column reference.</summary>
    /// <param name="columnName">The referenced column name.</param>
    /// <returns>An ascending <see cref="IndexColumn"/>.</returns>
    public static IndexColumn Ascending(string columnName) =>
        new() { ColumnName = columnName, Direction = SortDirection.Ascending };

    /// <summary>Creates a descending key column reference.</summary>
    /// <param name="columnName">The referenced column name.</param>
    /// <returns>A descending <see cref="IndexColumn"/>.</returns>
    public static IndexColumn Descending(string columnName) =>
        new() { ColumnName = columnName, Direction = SortDirection.Descending };
}
