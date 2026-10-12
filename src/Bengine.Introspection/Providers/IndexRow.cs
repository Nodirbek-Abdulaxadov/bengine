namespace Bengine.Introspection.Providers;

using Bengine.Enums;
using Bengine.Models;

/// <summary>
/// A flattened index/column row read from a system catalog, before grouping into
/// <see cref="Index"/> objects. Shared by the server providers, whose index
/// queries all return one row per index column.
/// </summary>
/// <param name="IndexName">The index name.</param>
/// <param name="IsUnique">Whether the index enforces uniqueness.</param>
/// <param name="ColumnName">The key column at this position.</param>
/// <param name="Descending">Whether the key column sorts descending.</param>
internal sealed record IndexRow(
    string IndexName,
    bool IsUnique,
    string ColumnName,
    bool Descending = false)
{
    /// <summary>Groups flattened rows into ordered <see cref="Index"/> objects, preserving column order.</summary>
    /// <param name="rows">The flattened rows, already ordered by index then key position.</param>
    /// <returns>One index per distinct name.</returns>
    public static IReadOnlyList<Index> Group(IReadOnlyList<IndexRow> rows) => rows
        .GroupBy(r => r.IndexName)
        .Select(group =>
        {
            var ordered = group.ToList();
            return new Index
            {
                Name = ordered[0].IndexName,
                Type = ordered[0].IsUnique ? IndexType.Unique : IndexType.NonUnique,
                Columns = ordered
                    .Select(r => new IndexColumn
                    {
                        ColumnName = r.ColumnName,
                        Direction = r.Descending ? SortDirection.Descending : SortDirection.Ascending,
                    })
                    .ToList(),
            };
        })
        .ToList();
}
