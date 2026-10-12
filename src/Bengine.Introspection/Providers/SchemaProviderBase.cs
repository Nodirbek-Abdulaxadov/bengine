namespace Bengine.Introspection.Providers;

using System.Data.Common;
using Bengine.Enums;
using Bengine.Introspection.Abstractions;
using Bengine.Introspection.Model;
using Bengine.Models;
using Bengine.Models.Options;

/// <summary>
/// Shared plumbing for the per-provider discovery and introspection
/// implementations: it holds the open connection and provider, exposes small
/// ADO.NET query helpers, and composes <see cref="IntrospectAsync"/> from the
/// per-relation <see cref="IntrospectTableAsync"/> that each subclass supplies.
/// </summary>
public abstract class SchemaProviderBase : IDatabaseExplorer, IDatabaseIntrospector
{
    /// <summary>Creates a provider bound to an open connection.</summary>
    /// <param name="connection">An open connection; the provider does not own or dispose it.</param>
    /// <param name="provider">The provider this instance speaks to.</param>
    protected SchemaProviderBase(DbConnection connection, DatabaseProvider provider)
    {
        Connection = connection;
        Provider = provider;
    }

    /// <summary>Gets the open connection this provider reads from.</summary>
    protected DbConnection Connection { get; }

    /// <summary>Gets the provider this instance speaks to.</summary>
    protected DatabaseProvider Provider { get; }

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<string>> GetSchemasAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<TableRef>> GetTablesAsync(string? schema = null, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<Table> IntrospectTableAsync(TableRef table, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public async Task<DatabaseSchema> IntrospectAsync(string? schema = null, CancellationToken cancellationToken = default)
    {
        var refs = await GetTablesAsync(schema, cancellationToken).ConfigureAwait(false);
        var tables = new List<Table>(refs.Count);
        foreach (var tableRef in refs)
        {
            if (tableRef.Kind != TableKind.Table)
            {
                continue;
            }

            tables.Add(await IntrospectTableAsync(tableRef, cancellationToken).ConfigureAwait(false));
        }

        return new DatabaseSchema
        {
            Name = schema ?? Connection.Database ?? "database",
            Tables = tables,
        };
    }

    /// <summary>Creates a command with the given text and optional parameters.</summary>
    /// <param name="sql">The command text.</param>
    /// <param name="parameters">Name/value pairs to bind.</param>
    /// <returns>A command ready to execute.</returns>
    protected DbCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    /// <summary>Runs a query and projects each row with <paramref name="read"/>.</summary>
    /// <typeparam name="T">The projected row type.</typeparam>
    /// <param name="sql">The query text.</param>
    /// <param name="read">Projects the current reader row.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="parameters">Name/value pairs to bind.</param>
    /// <returns>The projected rows.</returns>
    protected async Task<List<T>> QueryAsync<T>(
        string sql,
        Func<DbDataReader, T> read,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<T>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(read(reader));
            }

            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not Introspection.IntrospectionException)
        {
            throw new Introspection.IntrospectionException(
                $"A catalog query failed on {Provider}. {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Returns a copy of <paramref name="column"/> with its identity configuration
    /// replaced. <see cref="Column"/> is a sealed entity rather than a record, so
    /// callers cannot use <c>with</c>; this clones every field explicitly.
    /// </summary>
    /// <param name="column">The column to copy.</param>
    /// <param name="identity">The identity options to apply.</param>
    /// <returns>A new column identical to the original but for its identity.</returns>
    protected static Column WithIdentity(Column column, IdentityOptions identity) => new()
    {
        Name = column.Name,
        Type = column.Type,
        Nullability = column.Nullability,
        Default = column.Default,
        Identity = identity,
        Generated = column.Generated,
        Validation = column.Validation,
        Comment = column.Comment,
        ProviderOptions = column.ProviderOptions,
    };

    /// <summary>Reads a nullable string column by name, mapping <c>DBNull</c> to <c>null</c>.</summary>
    /// <param name="reader">The reader positioned on a row.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The string value, or <c>null</c>.</returns>
    protected static string? StringOrNull(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal)?.ToString();
    }

    /// <summary>Reads a required string column by name.</summary>
    /// <param name="reader">The reader positioned on a row.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The string value, or an empty string when null.</returns>
    protected static string String(DbDataReader reader, string column) =>
        StringOrNull(reader, column) ?? string.Empty;

    /// <summary>Reads a nullable 32-bit integer column by name, tolerating wider catalog integer types.</summary>
    /// <param name="reader">The reader positioned on a row.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The integer value, or <c>null</c>.</returns>
    protected static int? IntOrNull(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value is null or DBNull
            ? null
            : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
