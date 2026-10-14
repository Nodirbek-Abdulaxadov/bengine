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
/// measured around the command. A batch that returns several result sets (a SQL
/// Server batch, a procedure) yields one result per set.
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
        var results = await ExecuteAllAsync(sql, maxRows, cancellationToken).ConfigureAwait(false);
        return results.FirstOrDefault(r => r.IsQuery) ?? results[^1];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueryResult>> ExecuteAllAsync(
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

        await using var command = NewCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        PrepareForDisplay(command);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<QueryResult>();
            do
            {
                if (reader.FieldCount == 0)
                {
                    continue;
                }

                results.Add(await ReadResultSetAsync(reader, cap, stopwatch, cancellationToken).ConfigureAwait(false));
            }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

            // Completes the command so RecordsAffected is final.
            await reader.DisposeAsync().ConfigureAwait(false);
            stopwatch.Stop();
            if (results.Count == 0)
            {
                results.Add(new QueryResult
                {
                    IsQuery = false,
                    RowsAffected = reader.RecordsAffected,
                    ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                });
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not Introspection.IntrospectionException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The statement was cancelled.", ex, cancellationToken);
            }

            throw new Introspection.IntrospectionException(ex.Message, ex) { Position = ErrorPosition(ex) };
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<object?[]> StreamAsync(string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var command = NewCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        PrepareForDisplay(command);
        DbDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not Introspection.IntrospectionException)
        {
            throw new Introspection.IntrospectionException(ex.Message, ex);
        }

        await using (reader.ConfigureAwait(false))
        {
            var types = ColumnTypeNames(reader);
            while (true)
            {
                object?[] row;
                try
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        yield break;
                    }

                    row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                    {
                        var value = reader.GetValue(i);
                        row[i] = value is DBNull ? null : FromDisplayText(value, types[i]);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not Introspection.IntrospectionException)
                {
                    throw new Introspection.IntrospectionException(ex.Message, ex);
                }

                yield return row;
            }
        }
    }

    private async Task<QueryResult> ReadResultSetAsync(DbDataReader reader, int cap, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
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

    // The 1-based character position of a syntax error, when the server reports one (PostgreSQL).
    private static int? ErrorPosition(Exception ex) =>
        ex is Npgsql.PostgresException { Position: > 0 } pg ? pg.Position : null;
}
