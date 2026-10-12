namespace Bengine.Introspection.Providers;

using System.Data.Common;
using Bengine.Enums;
using Bengine.Introspection.Model;
using Bengine.Models;

/// <summary>
/// SQL Server discovery and introspection. Tables, columns, keys, and foreign
/// keys come from the shared <c>information_schema</c> implementation; identity
/// columns and secondary indexes — which the standard views do not expose — are
/// read from the <c>sys.*</c> catalog views.
/// </summary>
public sealed class SqlServerSchemaProvider : InformationSchemaProvider
{
    private static readonly HashSet<string> System =
        new(StringComparer.OrdinalIgnoreCase) { "sys", "INFORMATION_SCHEMA" };

    /// <summary>Creates a provider over an open SQL Server connection.</summary>
    /// <param name="connection">An open SQL Server connection.</param>
    public SqlServerSchemaProvider(DbConnection connection)
        : base(connection, DatabaseProvider.SqlServer)
    {
    }

    /// <inheritdoc />
    protected override IReadOnlySet<string> SystemSchemas => System;

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default) =>
        await QueryAsync(
            "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name",
            reader => String(reader, "name"),
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override async Task<IReadOnlySet<string>> ReadIdentityColumnsAsync(
        TableRef table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.name AS column_name
            FROM sys.identity_columns c
            JOIN sys.tables t ON c.object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE t.name = @t AND (@s IS NULL OR s.name = @s)
            """;

        var names = await QueryAsync(
            sql,
            reader => String(reader, "column_name"),
            cancellationToken,
            ("@t", table.Name),
            ("@s", table.Schema)).ConfigureAwait(false);

        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<Index>> ReadIndexesAsync(
        TableRef table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT i.name AS index_name, i.is_unique,
                   c.name AS column_name, ic.key_ordinal, ic.is_descending_key
            FROM sys.indexes i
            JOIN sys.tables t ON i.object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE t.name = @t AND (@s IS NULL OR s.name = @s)
              AND i.is_primary_key = 0 AND i.type > 0 AND ic.is_included_column = 0
            ORDER BY i.name, ic.key_ordinal
            """;

        var rows = await QueryAsync(
            sql,
            reader => new IndexRow(
                String(reader, "index_name"),
                reader.GetBoolean(reader.GetOrdinal("is_unique")),
                String(reader, "column_name"),
                reader.GetBoolean(reader.GetOrdinal("is_descending_key"))),
            cancellationToken,
            ("@t", table.Name),
            ("@s", table.Schema)).ConfigureAwait(false);

        return IndexRow.Group(rows);
    }
}
