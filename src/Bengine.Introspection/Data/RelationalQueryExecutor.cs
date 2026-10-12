namespace Bengine.Introspection.Data;

using System.Data.Common;
using System.Diagnostics;
using Bengine.Enums;
using Bengine.Introspection.Abstractions;
using Bengine.Introspection.Model;

/// <summary>
/// Runs ad-hoc SQL over an open connection for the query console. A result set is
/// materialized up to a row cap (reading one extra row to detect truncation); a
/// statement with no result set reports its affected-row count. Execution time is
/// measured around the command.
/// </summary>
public sealed class RelationalQueryExecutor : RelationalComponentBase, IQueryExecutor
{
    /// <summary>Creates an executor bound to an open connection.</summary>
    /// <param name="connection">An open connection; the executor does not own it.</param>
    /// <param name="provider">The provider whose dialect to emit.</param>
    public RelationalQueryExecutor(DbConnection connection, DatabaseProvider provider)
        : base(connection, provider)
    {
    }

    /// <inheritdoc />
    public async Task<QueryResult> ExecuteAsync(
        string sql,
        int maxRows = 1000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new Introspection.IntrospectionException("The statement is empty.");
        }

        var cap = Math.Clamp(maxRows, 1, 100_000);
        var stopwatch = Stopwatch.StartNew();

        await using var command = Connection.CreateCommand();
        command.CommandText = sql;
        PrepareForDisplay(command);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (reader.FieldCount == 0)
            {
                stopwatch.Stop();
                return new QueryResult
                {
                    IsQuery = false,
                    RowsAffected = reader.RecordsAffected,
                    ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                };
            }

            var columns = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns[i] = reader.GetName(i);
            }

            var types = ColumnTypeNames(reader);

            var rows = new List<IReadOnlyList<object?>>();
            var truncated = false;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count == cap)
                {
                    truncated = true;
                    break;
                }

                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    row[i] = value is DBNull ? null : FromDisplayText(value, types[i]);
                }

                rows.Add(row);
            }

            stopwatch.Stop();
            return new QueryResult
            {
                IsQuery = true,
                Columns = columns,
                ColumnTypes = types,
                Rows = rows,
                Truncated = truncated,
                ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not Introspection.IntrospectionException)
        {
            throw new Introspection.IntrospectionException(ex.Message, ex);
        }
    }
}
