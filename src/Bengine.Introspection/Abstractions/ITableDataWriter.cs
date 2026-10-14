namespace Bengine.Introspection.Abstractions;

using Bengine.Introspection.Model;

/// <summary>
/// Inserts, updates, and deletes rows of a relation on behalf of the data
/// editor. Column names are validated against the relation's real columns and
/// quoted; every value is bound as a parameter. Updates and deletes identify the
/// target rows by a key map (normally the primary key), which is rendered as a
/// parameterized <c>WHERE</c>; a call that would match no key columns is
/// rejected rather than risk a whole-table change. These methods modify live
/// data.
/// </summary>
public interface ITableDataWriter
{
    /// <summary>Inserts one row.</summary>
    /// <param name="table">The target relation.</param>
    /// <param name="values">The column/value pairs to insert.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows inserted.</returns>
    Task<int> InsertAsync(
        TableRef table,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default);

    /// <summary>Updates the rows matching a key.</summary>
    /// <param name="table">The target relation.</param>
    /// <param name="key">The column/value pairs identifying the row(s), combined with <c>AND</c>.</param>
    /// <param name="values">The column/value pairs to set.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows updated.</returns>
    Task<int> UpdateAsync(
        TableRef table,
        IReadOnlyDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the rows matching a key.</summary>
    /// <param name="table">The target relation.</param>
    /// <param name="key">The column/value pairs identifying the row(s), combined with <c>AND</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteAsync(
        TableRef table,
        IReadOnlyDictionary<string, object?> key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts many rows with one multi-row <c>INSERT</c>; every row must have
    /// the same columns. Used by imports, in batches the caller sizes to the
    /// provider's parameter limit.
    /// </summary>
    /// <param name="table">The target relation.</param>
    /// <param name="rows">The rows.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows inserted.</returns>
    Task<int> InsertManyAsync(
        TableRef table,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a data editor's pending changes in order, in one transaction — the
    /// session's open transaction when there is one, otherwise a transaction of
    /// its own that is committed at the end. Every update and delete must affect
    /// exactly one row; otherwise everything is rolled back.
    /// </summary>
    /// <param name="table">The target relation.</param>
    /// <param name="changes">The changes.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The affected-row count of each change.</returns>
    /// <exception cref="Introspection.IntrospectionException">A change failed; nothing was applied.</exception>
    Task<IReadOnlyList<int>> ApplyAsync(
        TableRef table,
        IReadOnlyList<RowChange> changes,
        CancellationToken cancellationToken = default);
}
