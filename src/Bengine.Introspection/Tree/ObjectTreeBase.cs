namespace Bengine.Introspection.Tree;

using System.Data.Common;
using System.Globalization;
using Bengine.Enums;

/// <summary>
/// Shared plumbing for the provider object trees: parameterized catalog queries
/// over the open connection, reader helpers, and builders for folder and item
/// nodes. Catalog failures surface as <see cref="IntrospectionException"/>.
/// </summary>
public abstract class ObjectTreeBase : IObjectTree
{
    /// <summary>Creates a tree over an open connection.</summary>
    /// <param name="connection">An open connection to the database the nodes belong to.</param>
    /// <param name="provider">The provider.</param>
    protected ObjectTreeBase(DbConnection connection, DatabaseProvider provider)
    {
        Connection = connection;
        Provider = provider;
    }

    /// <summary>Gets the open connection.</summary>
    protected DbConnection Connection { get; }

    /// <summary>Gets the provider.</summary>
    protected DatabaseProvider Provider { get; }

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<TreeNode>> GetChildrenAsync(NodeRef node, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<ObjectProperties> GetPropertiesAsync(NodeRef node, CancellationToken cancellationToken = default);

    /// <summary>Creates a folder node.</summary>
    /// <param name="parent">The node the folder belongs to (already scoped).</param>
    /// <param name="kind">The folder kind.</param>
    /// <param name="label">The display label.</param>
    /// <param name="count">The number of children, or <c>null</c> if unknown.</param>
    /// <returns>The folder node.</returns>
    protected static TreeNode Folder(NodeRef parent, NodeKind kind, string label, long? count) => new()
    {
        Ref = parent.Folder(kind),
        Label = label,
        Count = count,
        HasChildren = count is null or > 0,
    };

    /// <summary>Creates an item node inside a folder.</summary>
    /// <param name="folder">The folder the item is listed in.</param>
    /// <param name="kind">The item kind.</param>
    /// <param name="name">The object name.</param>
    /// <param name="hasChildren">Whether the item can be expanded.</param>
    /// <param name="label">The display label, defaulting to the name.</param>
    /// <param name="detail">Optional dimmed secondary text.</param>
    /// <param name="badge">Optional badge.</param>
    /// <param name="signature">Optional discriminator for non-unique names.</param>
    /// <returns>The item node.</returns>
    protected static TreeNode Item(
        NodeRef folder,
        NodeKind kind,
        string name,
        bool hasChildren = false,
        string? label = null,
        string? detail = null,
        string? badge = null,
        string? signature = null) => new()
    {
        Ref = folder.Item(kind, name, signature),
        Label = label ?? name,
        Detail = detail,
        Badge = badge,
        HasChildren = hasChildren,
    };

    /// <summary>Runs a catalog query and projects each row.</summary>
    /// <typeparam name="T">The projected row type.</typeparam>
    /// <param name="sql">The query text.</param>
    /// <param name="read">Projects the current row.</param>
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
            var rows = new List<T>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(read(reader));
            }

            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IntrospectionException)
        {
            throw new IntrospectionException(ex.Message, ex);
        }
    }

    /// <summary>Runs a query and returns its first row as a column-name map, or <c>null</c> if it returned none.</summary>
    /// <param name="sql">The query text.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="parameters">Name/value pairs to bind.</param>
    /// <returns>The first row, keyed case-insensitively by column name.</returns>
    protected async Task<Dictionary<string, object?>?> RowAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var rows = await QueryAsync(
            sql,
            reader =>
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                return row;
            },
            cancellationToken,
            parameters).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// Runs a query and returns its single value, or <c>null</c> when the query
    /// fails — used for optional catalog reads that may need privileges the user
    /// lacks (a count shown as unknown rather than failing the whole level).
    /// </summary>
    /// <param name="sql">The query text.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="parameters">Name/value pairs to bind.</param>
    /// <returns>The value, or <c>null</c>.</returns>
    protected async Task<object?> TryScalarAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        try
        {
            await using var command = Command(sql, parameters);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is DBNull ? null : value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Formats a raw catalog value for display.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The display text, or <c>null</c>.</returns>
    protected static string? Text(object? value) => value switch
    {
        null or DBNull => null,
        bool b => b ? "Yes" : "No",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        string[] array => string.Join(", ", array),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>Reads a value from a row map as a long.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The value, or <c>null</c>.</returns>
    protected static long? Long(Dictionary<string, object?>? row, string column) =>
        row is not null && row.TryGetValue(column, out var value) && value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>Reads a value from a row map as display text.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The text, or <c>null</c>.</returns>
    protected static string? Get(Dictionary<string, object?>? row, string column) =>
        row is not null && row.TryGetValue(column, out var value) ? Text(value) : null;

    /// <summary>Reads a string column from a reader.</summary>
    /// <param name="reader">The reader on a row.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The value, or an empty string when null.</returns>
    protected static string Str(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Reads a nullable string column from a reader.</summary>
    /// <param name="reader">The reader on a row.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The value, or <c>null</c>.</returns>
    protected static string? StrOrNull(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a boolean column from a reader, tolerating integer encodings.</summary>
    /// <param name="reader">The reader on a row.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The value; <c>false</c> when null.</returns>
    protected static bool Bool(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool b => b,
            string s => s is "1" or "Y" or "y" or "YES" or "yes" or "t" or "true",
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
        };
    }

    /// <summary>Reads a nullable long column from a reader.</summary>
    /// <param name="reader">The reader on a row.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The value, or <c>null</c>.</returns>
    protected static long? LongOrNull(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    /// <summary>Builds a properties list, dropping entries whose value is <c>null</c>.</summary>
    /// <param name="items">Name/value pairs.</param>
    /// <returns>The non-null properties.</returns>
    protected static IReadOnlyList<PropertyItem> Props(params (string Name, string? Value)[] items) =>
        items.Where(i => i.Value is not null).Select(i => new PropertyItem(i.Name, i.Value)).ToList();

    private DbCommand Command(string sql, (string Name, object? Value)[] parameters)
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
}
