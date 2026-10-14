namespace Bengine.Introspection.Data;

using System.Data.Common;
using Bengine.Enums;
using Bengine.Generators.Sql;
using Bengine.Introspection.Model;

/// <summary>
/// Shared plumbing for the relational data components (reader, writer, query
/// executor): the open connection, the provider's <see cref="SqlDialect"/> for
/// identifier quoting, schema-qualified name rendering, and a column probe used
/// to validate caller-supplied column names before they are quoted into SQL.
/// </summary>
public abstract class RelationalComponentBase
{
    /// <summary>Creates a component bound to an open connection.</summary>
    /// <param name="connection">An open connection; the component does not own it.</param>
    /// <param name="provider">The provider whose dialect to emit.</param>
    protected RelationalComponentBase(DbConnection connection, DatabaseProvider provider)
    {
        Connection = connection;
        Provider = provider;
        Dialect = DialectFactory.For(provider);
    }

    /// <summary>Gets the open connection.</summary>
    protected DbConnection Connection { get; }

    /// <summary>Gets the provider.</summary>
    protected DatabaseProvider Provider { get; }

    /// <summary>Gets the provider's dialect, used for identifier quoting.</summary>
    protected SqlDialect Dialect { get; }

    /// <summary>
    /// Gets the source of the session's open transaction, if any. Drivers such as
    /// MySqlConnector and SqlClient require every command run while a transaction
    /// is open to be enlisted in it.
    /// </summary>
    public Func<DbTransaction?>? TransactionSource { get; init; }

    /// <summary>Gets the transaction new commands enlist in.</summary>
    protected virtual DbTransaction? CurrentTransaction => TransactionSource?.Invoke();

    /// <summary>Creates a command on the connection, enlisted in the current transaction.</summary>
    /// <returns>A new command.</returns>
    protected DbCommand NewCommand()
    {
        var command = Connection.CreateCommand();
        command.Transaction = CurrentTransaction;
        return command;
    }

    /// <summary>Renders a relation's schema-qualified, quoted name.</summary>
    /// <param name="table">The relation.</param>
    /// <returns>The quoted, optionally schema-qualified name.</returns>
    protected string QualifiedName(TableRef table) => table.Schema is { Length: > 0 } schema
        ? $"{Dialect.QuoteIdentifier(schema)}.{Dialect.QuoteIdentifier(table.Name)}"
        : Dialect.QuoteIdentifier(table.Name);

    /// <summary>Reads the relation's column names without transferring any rows.</summary>
    /// <param name="table">The relation to inspect.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The column names in result order.</returns>
    /// <exception cref="Introspection.IntrospectionException">The columns could not be read.</exception>
    protected async Task<IReadOnlyList<string>> ReadColumnNamesAsync(TableRef table, CancellationToken cancellationToken)
    {
        var sql = $"SELECT * FROM {QualifiedName(table)} WHERE 1 = 0";
        await using var command = NewCommand();
        command.CommandText = sql;
        try
        {
            await using var reader = await command
                .ExecuteReaderAsync(System.Data.CommandBehavior.SchemaOnly, cancellationToken)
                .ConfigureAwait(false);
            var names = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                names[i] = reader.GetName(i);
            }

            return names;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new Introspection.IntrospectionException(
                $"Could not read columns of {table.QualifiedName}. {ex.Message}", ex);
        }
    }

    /// <summary>Validates a column name against a set, throwing if it is unknown.</summary>
    /// <param name="column">The caller-supplied column name.</param>
    /// <param name="columns">The relation's real columns.</param>
    /// <exception cref="Introspection.IntrospectionException">The column is not part of the relation.</exception>
    protected static void EnsureKnownColumn(string column, IReadOnlySet<string> columns)
    {
        if (!columns.Contains(column))
        {
            throw new Introspection.IntrospectionException(
                $"Unknown column '{column}'. Only columns of the relation may be referenced.");
        }
    }

    /// <summary>Adds a parameter to the list and returns its placeholder name.</summary>
    /// <param name="parameters">The accumulating parameter list.</param>
    /// <param name="value">The value to bind (<c>null</c> becomes <c>DBNull</c>).</param>
    /// <returns>The <c>@pN</c> placeholder.</returns>
    protected static string AddParameter(List<(string Name, object Value)> parameters, object? value)
    {
        var name = "@p" + parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        parameters.Add((name, value ?? DBNull.Value));
        return name;
    }

    /// <summary>Creates a command with the given text and bound parameters.</summary>
    /// <param name="sql">The command text.</param>
    /// <param name="parameters">The parameters to bind.</param>
    /// <returns>A command ready to execute.</returns>
    protected DbCommand CreateCommand(string sql, List<(string Name, object Value)> parameters)
    {
        var command = NewCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;

            // Values typed in a UI arrive as text. Bound as `text`, PostgreSQL
            // rejects them against uuid, timestamp, enum, or bigint columns
            // (`uuid = text`); sent untyped, the server infers the type from the
            // column the value is compared with or assigned to.
            if (value is string && parameter is Npgsql.NpgsqlParameter npgsql)
            {
                npgsql.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Unknown;
            }

            command.Parameters.Add(parameter);
        }

        return command;
    }

    /// <summary>
    /// Prepares a command whose result set is shown to a user. For PostgreSQL every
    /// result column is requested in text format: some types (<c>aclitem</c>, many
    /// extension types) have no binary output and would otherwise fail the whole
    /// read, and text is exactly what PostgreSQL itself prints — exact numerics,
    /// server-formatted timestamps, array and JSON literals — as pgAdmin shows.
    /// </summary>
    /// <param name="command">The command about to run.</param>
    protected static void PrepareForDisplay(DbCommand command)
    {
        if (command is Npgsql.NpgsqlCommand npgsql)
        {
            npgsql.AllResultTypesAreUnknown = true;
        }
    }

    /// <summary>
    /// Converts a PostgreSQL text-format value back to a native value where that
    /// is lossless — booleans and integers — leaving everything else as the
    /// server's own text. Non-PostgreSQL values pass through unchanged.
    /// </summary>
    /// <param name="value">The value read from the result.</param>
    /// <param name="typeName">The column's database type name.</param>
    /// <returns>The display value.</returns>
    protected object? FromDisplayText(object? value, string typeName)
    {
        if (Provider != DatabaseProvider.PostgreSql || value is not string text)
        {
            return value;
        }

        return typeName switch
        {
            "boolean" or "bool" => text switch { "t" => true, "f" => false, _ => text },
            "smallint" or "integer" or "bigint" or "oid" =>
                long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : text,
            _ => text,
        };
    }

    /// <summary>Reads the database type name of every result column, tolerating drivers that cannot report one.</summary>
    /// <param name="reader">An open reader.</param>
    /// <returns>The type names, aligned with the result columns.</returns>
    protected static string[] ColumnTypeNames(DbDataReader reader)
    {
        var types = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            try
            {
                types[i] = reader.GetDataTypeName(i);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IndexOutOfRangeException)
            {
                types[i] = string.Empty;
            }
        }

        return types;
    }

    /// <summary>
    /// Renders a column as text so a pattern match works whatever the column's
    /// type (numbers, dates, uuids, JSON).
    /// </summary>
    /// <param name="quotedColumn">The quoted column reference.</param>
    /// <returns>The provider's cast-to-text expression.</returns>
    protected string AsText(string quotedColumn) => Provider switch
    {
        DatabaseProvider.MySql => $"CAST({quotedColumn} AS CHAR)",
        DatabaseProvider.SqlServer => $"CAST({quotedColumn} AS NVARCHAR(MAX))",
        _ => $"CAST({quotedColumn} AS TEXT)",
    };
}
