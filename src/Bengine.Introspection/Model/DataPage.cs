namespace Bengine.Introspection.Model;

/// <summary>
/// One page of rows read from a relation. Columns are reported separately from
/// rows so a grid can bind headers once; each row is a positional list aligned
/// with <see cref="Columns"/>. Cell values are the raw CLR values the driver
/// produced, with database <c>NULL</c> represented as <c>null</c> (never
/// <see cref="System.DBNull"/>).
/// </summary>
public sealed record DataPage
{
    /// <summary>Gets the column names in result order.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>Gets the database type name of each column (for example <c>integer</c>, <c>timestamp with time zone</c>), aligned with <see cref="Columns"/>.</summary>
    public IReadOnlyList<string> ColumnTypes { get; init; } = [];

    /// <summary>Gets the rows, each a positional list of cell values aligned with <see cref="Columns"/>.</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }

    /// <summary>Gets the one-based page number this result represents.</summary>
    public required int Page { get; init; }

    /// <summary>Gets the page size that was applied.</summary>
    public required int PageSize { get; init; }

    /// <summary>
    /// Gets the total number of rows matching the filter, or <c>null</c> when the
    /// query did not request a count.
    /// </summary>
    public long? TotalCount { get; init; }

    /// <summary>
    /// Gets a value indicating whether at least one further row exists beyond this
    /// page. Determined by reading one extra row, so it is accurate even when
    /// <see cref="TotalCount"/> was not requested.
    /// </summary>
    public required bool HasMore { get; init; }

    /// <summary>Gets the number of rows returned in this page.</summary>
    public int RowCount => Rows.Count;
}
