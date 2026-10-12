namespace Bengine.Introspection.Abstractions;

using Bengine.Introspection.Model;

/// <summary>
/// Runs an ad-hoc SQL statement typed into the query console and returns either
/// its result set or its affected-row count. Unlike the data reader, the console
/// runs the SQL verbatim — it is the user's own statement — so it is only as safe
/// as the connection's permissions; open sessions against databases you are
/// authorized to query.
/// </summary>
public interface IQueryExecutor
{
    /// <summary>Executes one SQL statement.</summary>
    /// <param name="sql">The SQL to run.</param>
    /// <param name="maxRows">The most rows to materialize from a result set; extra rows are dropped and the result is flagged truncated.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The result set or affected-row count, with timing.</returns>
    /// <exception cref="Introspection.IntrospectionException">The statement failed.</exception>
    Task<QueryResult> ExecuteAsync(string sql, int maxRows = 1000, CancellationToken cancellationToken = default);
}
