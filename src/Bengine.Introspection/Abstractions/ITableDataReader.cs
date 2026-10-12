namespace Bengine.Introspection.Abstractions;

using Bengine.Introspection.Model;

/// <summary>
/// Reads rows of data from a relation for browsing, with paging, filtering, and
/// sorting. Implementations build only parameterized SQL: identifiers are
/// validated against the relation's real columns and quoted, and every value is
/// bound as a parameter, so a <see cref="DataQuery"/> can never inject SQL.
/// </summary>
public interface ITableDataReader
{
    /// <summary>Reads one page of rows from the relation.</summary>
    /// <param name="table">The relation to read.</param>
    /// <param name="query">The page, filters, and ordering to apply.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The requested page of rows.</returns>
    Task<DataPage> QueryAsync(TableRef table, DataQuery query, CancellationToken cancellationToken = default);
}
