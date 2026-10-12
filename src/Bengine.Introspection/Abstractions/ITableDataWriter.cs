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
}
