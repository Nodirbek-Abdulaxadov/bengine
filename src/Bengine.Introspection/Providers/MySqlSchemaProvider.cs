namespace Bengine.Introspection.Providers;

using System.Data.Common;
using Bengine.Enums;
using Bengine.Introspection.Model;
using Bengine.Models;

/// <summary>
/// MySQL discovery and introspection. In MySQL a schema and a database are the
/// same thing, so <c>GetDatabasesAsync</c> and <c>GetSchemasAsync</c>
/// return the same user namespaces. Identity columns are detected from the
/// <c>auto_increment</c> extra, and indexes from <c>information_schema.statistics</c>.
/// </summary>
public sealed class MySqlSchemaProvider : InformationSchemaProvider
{
    private static readonly HashSet<string> System = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema", "mysql", "performance_schema", "sys",
    };

    /// <summary>Creates a provider over an open MySQL connection.</summary>
    /// <param name="connection">An open MySQL connection.</param>
    public MySqlSchemaProvider(DbConnection connection)
        : base(connection, DatabaseProvider.MySql)
    {
    }

    /// <inheritdoc />
    protected override IReadOnlySet<string> SystemSchemas => System;

    /// <inheritdoc />
    public override Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default) =>
        GetSchemasAsync(cancellationToken);

    /// <inheritdoc />
    protected override async Task<IReadOnlySet<string>> ReadIdentityColumnsAsync(
        TableRef table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_name = @t AND (@s IS NULL OR table_schema = @s)
              AND extra LIKE '%auto_increment%'
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
            SELECT index_name, non_unique, column_name, seq_in_index
            FROM information_schema.statistics
            WHERE table_name = @t AND (@s IS NULL OR table_schema = @s)
              AND index_name <> 'PRIMARY'
            ORDER BY index_name, seq_in_index
            """;

        var rows = await QueryAsync(
            sql,
            reader => new IndexRow(
                String(reader, "index_name"),
                IntOrNull(reader, "non_unique") == 0,
                String(reader, "column_name")),
            cancellationToken,
            ("@t", table.Name),
            ("@s", table.Schema)).ConfigureAwait(false);

        return IndexRow.Group(rows);
    }
}
