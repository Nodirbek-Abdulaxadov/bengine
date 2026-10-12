namespace Bengine.Introspection.Model;

/// <summary>
/// The outcome of running an ad-hoc SQL statement from the query console. A
/// statement that returns rows (a <c>SELECT</c>, or anything with a result set)
/// sets <see cref="IsQuery"/> and fills <see cref="Columns"/>/<see cref="Rows"/>;
/// a statement that does not (<c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c>/DDL)
/// reports <see cref="RowsAffected"/> instead.
/// </summary>
public sealed record QueryResult
{
    /// <summary>Gets a value indicating whether the statement returned a result set.</summary>
    public required bool IsQuery { get; init; }

    /// <summary>Gets the result column names, empty for a non-query.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Gets the database type name of each result column, aligned with <see cref="Columns"/>.</summary>
    public IReadOnlyList<string> ColumnTypes { get; init; } = [];

    /// <summary>Gets the result rows, each aligned with <see cref="Columns"/>; empty for a non-query.</summary>
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = [];

    /// <summary>Gets the number of rows affected by a non-query statement; <c>-1</c> when not applicable.</summary>
    public long RowsAffected { get; init; } = -1;

    /// <summary>Gets a value indicating whether the result set was truncated at the row cap.</summary>
    public bool Truncated { get; init; }

    /// <summary>Gets how long the statement took to execute, in milliseconds.</summary>
    public required double ElapsedMilliseconds { get; init; }
}
