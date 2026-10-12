namespace Bengine.Introspection.Data;

using System.Data.Common;
using Bengine.Enums;
using Bengine.Introspection.Abstractions;
using Bengine.Introspection.Model;

/// <summary>
/// A provider-independent <see cref="ITableDataWriter"/>. Each call probes the
/// relation's real columns, validates every supplied column against them, quotes
/// identifiers with the provider's dialect, and binds all values as parameters,
/// so a row edit can neither reference an unknown column nor inject SQL. Updates
/// and deletes require a non-empty key and build a parameterized <c>WHERE</c> from
/// it.
/// </summary>
public sealed class RelationalDataWriter : RelationalComponentBase, ITableDataWriter
{
    /// <summary>Creates a writer bound to an open connection for the given provider.</summary>
    /// <param name="connection">An open connection; the writer does not own or dispose it.</param>
    /// <param name="provider">The provider whose SQL dialect to emit.</param>
    public RelationalDataWriter(DbConnection connection, DatabaseProvider provider)
        : base(connection, provider)
    {
    }

    /// <inheritdoc />
    public async Task<int> InsertAsync(
        TableRef table,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        if (values.Count == 0)
        {
            throw new Introspection.IntrospectionException("An insert needs at least one column value.");
        }

        var columns = await ColumnSetAsync(table, cancellationToken).ConfigureAwait(false);
        var parameters = new List<(string Name, object Value)>();
        var names = new List<string>(values.Count);
        var placeholders = new List<string>(values.Count);

        foreach (var (column, value) in values)
        {
            EnsureKnownColumn(column, columns);
            names.Add(Dialect.QuoteIdentifier(column));
            placeholders.Add(AddParameter(parameters, value));
        }

        var sql = $"INSERT INTO {QualifiedName(table)} ({string.Join(", ", names)}) " +
                  $"VALUES ({string.Join(", ", placeholders)})";

        return await ExecuteAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> UpdateAsync(
        TableRef table,
        IReadOnlyDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        if (values.Count == 0)
        {
            throw new Introspection.IntrospectionException("An update needs at least one column to set.");
        }

        RequireKey(key);
        var columns = await ColumnSetAsync(table, cancellationToken).ConfigureAwait(false);
        var parameters = new List<(string Name, object Value)>();

        var assignments = new List<string>(values.Count);
        foreach (var (column, value) in values)
        {
            EnsureKnownColumn(column, columns);
            assignments.Add($"{Dialect.QuoteIdentifier(column)} = {AddParameter(parameters, value)}");
        }

        var where = BuildKeyPredicate(key, columns, parameters);
        var sql = $"UPDATE {QualifiedName(table)} SET {string.Join(", ", assignments)} WHERE {where}";

        return await ExecuteAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> DeleteAsync(
        TableRef table,
        IReadOnlyDictionary<string, object?> key,
        CancellationToken cancellationToken = default)
    {
        RequireKey(key);
        var columns = await ColumnSetAsync(table, cancellationToken).ConfigureAwait(false);
        var parameters = new List<(string Name, object Value)>();
        var where = BuildKeyPredicate(key, columns, parameters);
        var sql = $"DELETE FROM {QualifiedName(table)} WHERE {where}";

        return await ExecuteAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    private string BuildKeyPredicate(
        IReadOnlyDictionary<string, object?> key,
        IReadOnlySet<string> columns,
        List<(string Name, object Value)> parameters)
    {
        var clauses = new List<string>(key.Count);
        foreach (var (column, value) in key)
        {
            EnsureKnownColumn(column, columns);
            var quoted = Dialect.QuoteIdentifier(column);
            clauses.Add(value is null
                ? $"{quoted} IS NULL"
                : $"{quoted} = {AddParameter(parameters, value)}");
        }

        return string.Join(" AND ", clauses);
    }

    private async Task<IReadOnlySet<string>> ColumnSetAsync(TableRef table, CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(table, cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<int> ExecuteAsync(
        string sql,
        List<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
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

    private static void RequireKey(IReadOnlyDictionary<string, object?> key)
    {
        if (key.Count == 0)
        {
            throw new Introspection.IntrospectionException(
                "A key is required to update or delete a row; refusing to match every row.");
        }
    }
}
