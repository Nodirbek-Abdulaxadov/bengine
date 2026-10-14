namespace Bengine.Introspection.Data;

using System.Data.Common;
using Bengine.Enums;
using Bengine.Introspection.Abstractions;
using Bengine.Introspection.Export;
using Bengine.Introspection.Model;

/// <summary>
/// A provider-independent <see cref="ITableDataWriter"/>. Each call probes the
/// relation's real columns, validates every supplied column against them, quotes
/// identifiers with the provider's dialect, and binds all values as parameters,
/// so a row edit can neither reference an unknown column nor inject SQL. Updates
/// and deletes require a non-empty key and build a parameterized <c>WHERE</c> from
/// it. A change set is applied in one transaction (the session's, when one is
/// open) and rolled back if any update or delete does not hit exactly one row.
/// </summary>
public sealed class RelationalDataWriter : RelationalComponentBase, ITableDataWriter
{
    private DbTransaction? _local;

    /// <summary>Creates a writer bound to an open connection for the given provider.</summary>
    /// <param name="connection">An open connection; the writer does not own or dispose it.</param>
    /// <param name="provider">The provider whose SQL dialect to emit.</param>
    public RelationalDataWriter(DbConnection connection, DatabaseProvider provider)
        : base(connection, provider)
    {
    }

    /// <inheritdoc />
    protected override DbTransaction? CurrentTransaction => _local ?? base.CurrentTransaction;

    /// <inheritdoc />
    public async Task<int> InsertAsync(
        TableRef table,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        var columns = await ColumnSetAsync(table, cancellationToken).ConfigureAwait(false);
        return await RunAsync(table, RowChange.Insert(values), columns, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> UpdateAsync(
        TableRef table,
        IReadOnlyDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        var columns = await ColumnSetAsync(table, cancellationToken).ConfigureAwait(false);
        return await RunAsync(table, RowChange.Update(key, values), columns, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> DeleteAsync(
        TableRef table,
        IReadOnlyDictionary<string, object?> key,
        CancellationToken cancellationToken = default)
    {
        var columns = await ColumnSetAsync(table, cancellationToken).ConfigureAwait(false);
        return await RunAsync(table, RowChange.Delete(key), columns, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> InsertManyAsync(
        TableRef table,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var columns = await ColumnSetAsync(table, cancellationToken).ConfigureAwait(false);
        var names = rows[0].Keys.ToList();
        foreach (var name in names)
        {
            EnsureKnownColumn(name, columns);
        }

        var parameters = new List<(string Name, object Value)>();
        var tuples = rows.Select(row => "(" + string.Join(", ", names.Select(n => AddParameter(parameters, row.GetValueOrDefault(n)))) + ")");
        var sql = $"INSERT INTO {QualifiedName(table)} ({string.Join(", ", names.Select(Dialect.QuoteIdentifier))}) VALUES {string.Join(", ", tuples)}";
        await using var command = CreateCommand(sql, parameters);
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new Introspection.IntrospectionException(ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ApplyAsync(
        TableRef table,
        IReadOnlyList<RowChange> changes,
        CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
        {
            return [];
        }

        var columns = await ColumnSetAsync(table, cancellationToken).ConfigureAwait(false);
        var ownsTransaction = base.CurrentTransaction is null;
        if (ownsTransaction)
        {
            _local = await Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var counts = new List<int>(changes.Count);
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                var affected = await RunAsync(table, change, columns, cancellationToken).ConfigureAwait(false);
                if (change.Kind != RowChangeKind.Insert && affected != 1)
                {
                    throw new Introspection.IntrospectionException(
                        $"Change {i + 1} ({change.Kind.ToString().ToLowerInvariant()}) matched {affected} rows instead of one; " +
                        "the row was changed or deleted by someone else, or the key is not unique. Nothing was saved.");
                }

                counts.Add(affected);
            }

            if (_local is not null)
            {
                await _local.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return counts;
        }
        catch
        {
            if (_local is not null)
            {
                await _local.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (_local is not null)
            {
                await _local.DisposeAsync().ConfigureAwait(false);
                _local = null;
            }
        }
    }

    /// <summary>
    /// Renders a change set as SQL with the values inlined as literals, for a
    /// "preview pending changes" view or "copy as SQL". The statements are what
    /// <see cref="ApplyAsync"/> runs, except that it binds parameters instead.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="table">The relation.</param>
    /// <param name="changes">The changes.</param>
    /// <returns>One statement per change, each ending in <c>;</c>.</returns>
    public static IReadOnlyList<string> Preview(DatabaseProvider provider, TableRef table, IReadOnlyList<RowChange> changes)
    {
        var dialect = DialectFactory.For(provider);
        var qualified = table.Schema is { Length: > 0 } schema
            ? $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(table.Name)}"
            : dialect.QuoteIdentifier(table.Name);
        return changes
            .Select(c => Build(provider, qualified, c, dialect.QuoteIdentifier, value => SqlLiterals.Format(value, provider), null) + ";")
            .ToList();
    }

    private async Task<int> RunAsync(TableRef table, RowChange change, IReadOnlySet<string> columns, CancellationToken cancellationToken)
    {
        var parameters = new List<(string Name, object Value)>();
        var sql = Build(Provider, QualifiedName(table), change, Dialect.QuoteIdentifier, value => AddParameter(parameters, value), columns);
        await using var command = CreateCommand(sql, parameters);
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new Introspection.IntrospectionException(ex.Message, ex);
        }
    }

    private static string Build(
        DatabaseProvider provider,
        string qualified,
        RowChange change,
        Func<string, string> quote,
        Func<object?, string> value,
        IReadOnlySet<string>? columns)
    {
        void Check(string column)
        {
            if (columns is not null)
            {
                EnsureKnownColumn(column, columns);
            }
        }

        string Where()
        {
            if (change.Key.Count == 0)
            {
                throw new Introspection.IntrospectionException(
                    "A key is required to update or delete a row; refusing to match every row.");
            }

            return string.Join(" AND ", change.Key.Select(pair =>
            {
                Check(pair.Key);
                return pair.Value is null ? $"{quote(pair.Key)} IS NULL" : $"{quote(pair.Key)} = {value(pair.Value)}";
            }));
        }

        switch (change.Kind)
        {
            case RowChangeKind.Insert:
            {
                // DEFAULT cells are left out so the server fills them in (SQLite has no DEFAULT in VALUES).
                var written = change.Values.Where(p => p.Value is not SqlDefault).ToList();
                foreach (var pair in written)
                {
                    Check(pair.Key);
                }

                return written.Count == 0
                    ? provider == DatabaseProvider.MySql ? $"INSERT INTO {qualified} () VALUES ()" : $"INSERT INTO {qualified} DEFAULT VALUES"
                    : $"INSERT INTO {qualified} ({string.Join(", ", written.Select(p => quote(p.Key)))}) " +
                      $"VALUES ({string.Join(", ", written.Select(p => value(p.Value)))})";
            }

            case RowChangeKind.Update:
            {
                if (change.Values.Count == 0)
                {
                    throw new Introspection.IntrospectionException("An update needs at least one column to set.");
                }

                var assignments = change.Values.Select(pair =>
                {
                    Check(pair.Key);
                    return $"{quote(pair.Key)} = {(pair.Value is SqlDefault ? "DEFAULT" : value(pair.Value))}";
                }).ToList();
                return $"UPDATE {qualified} SET {string.Join(", ", assignments)} WHERE {Where()}";
            }

            default:
                return $"DELETE FROM {qualified} WHERE {Where()}";
        }
    }

    private async Task<IReadOnlySet<string>> ColumnSetAsync(TableRef table, CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(table, cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
    }
}
