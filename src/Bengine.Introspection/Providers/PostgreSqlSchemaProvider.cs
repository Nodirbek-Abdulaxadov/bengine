namespace Bengine.Introspection.Providers;

using System.Data.Common;
using Bengine.Enums;
using Bengine.Introspection.Model;
using Bengine.Models;

/// <summary>
/// PostgreSQL discovery and introspection. Tables, columns, keys, and foreign
/// keys come from the shared <c>information_schema</c> implementation; identity
/// columns (both <c>GENERATED ... AS IDENTITY</c> and <c>serial</c> defaults)
/// and secondary indexes are read from the <c>pg_catalog</c> system tables.
/// </summary>
public sealed class PostgreSqlSchemaProvider : InformationSchemaProvider
{
    private static readonly HashSet<string> System =
        new(StringComparer.OrdinalIgnoreCase) { "pg_catalog", "information_schema", "pg_toast" };

    /// <summary>Creates a provider over an open PostgreSQL connection.</summary>
    /// <param name="connection">An open PostgreSQL connection.</param>
    public PostgreSqlSchemaProvider(DbConnection connection)
        : base(connection, DatabaseProvider.PostgreSql)
    {
    }

    /// <inheritdoc />
    protected override IReadOnlySet<string> SystemSchemas => System;

    /// <inheritdoc />
    protected override string ColumnCommentSql =>
        "col_description((quote_ident(table_schema) || '.' || quote_ident(table_name))::regclass, ordinal_position::int)";

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<CheckConstraint>> ReadChecksAsync(TableRef table, CancellationToken cancellationToken) =>
        await QueryAsync(
            """
            SELECT conname, pg_get_constraintdef(oid) AS def FROM pg_constraint
            WHERE contype = 'c' AND conrelid = (quote_ident(coalesce(@s, current_schema())) || '.' || quote_ident(@t))::regclass
            ORDER BY conname
            """,
            reader => new CheckConstraint { Name = String(reader, "conname"), Expression = Unwrap(String(reader, "def")) },
            cancellationToken,
            ("@t", table.Name),
            ("@s", table.Schema)).ConfigureAwait(false);

    /// <inheritdoc />
    protected override async Task<string?> ReadTableCommentAsync(TableRef table, CancellationToken cancellationToken) =>
        (await QueryAsync(
            "SELECT obj_description((quote_ident(coalesce(@s, current_schema())) || '.' || quote_ident(@t))::regclass, 'pg_class') AS c",
            reader => StringOrNull(reader, "c"),
            cancellationToken,
            ("@t", table.Name),
            ("@s", table.Schema)).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    protected override string NativeTypeSql => """
        (SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a
         WHERE a.attrelid = (quote_ident(table_schema) || '.' || quote_ident(table_name))::regclass
           AND a.attname = column_name AND NOT a.attisdropped)
        """;

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default) =>
        await QueryAsync(
            "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname",
            reader => String(reader, "datname"),
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override async Task<IReadOnlySet<string>> ReadIdentityColumnsAsync(
        TableRef table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_name = @t AND (@s IS NULL OR table_schema = @s)
              AND (is_identity = 'YES' OR column_default LIKE 'nextval(%')
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
            SELECT i.relname AS index_name, ix.indisunique AS is_unique,
                   a.attname AS column_name, k.ord AS position
            FROM pg_class t
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_index ix ON ix.indrelid = t.oid
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE t.relname = @t AND (@s IS NULL OR n.nspname = @s)
              AND ix.indisprimary = false
            ORDER BY i.relname, k.ord
            """;

        var rows = await QueryAsync(
            sql,
            reader => new IndexRow(
                String(reader, "index_name"),
                reader.GetBoolean(reader.GetOrdinal("is_unique")),
                String(reader, "column_name")),
            cancellationToken,
            ("@t", table.Name),
            ("@s", table.Schema)).ConfigureAwait(false);

        return IndexRow.Group(rows);
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<View>> ReadViewsAsync(string? schema, CancellationToken cancellationToken)
    {
        var filter = schema is null ? "NOT IN ('pg_catalog', 'information_schema') AND schemaname NOT LIKE 'pg\\_%'" : "= @s";
        var sql = $"""
            SELECT schemaname AS s, viewname AS n, definition AS d, false AS m FROM pg_views WHERE schemaname {filter}
            UNION ALL
            SELECT schemaname, matviewname, definition, true FROM pg_matviews WHERE schemaname {filter}
            ORDER BY 1, 2
            """;
        return await QueryAsync(
            sql,
            reader => new View
            {
                Schema = String(reader, "s"),
                Name = String(reader, "n"),
                Definition = String(reader, "d").Trim().TrimEnd(';'),
                IsMaterialized = reader.GetBoolean(reader.GetOrdinal("m")),
            },
            cancellationToken,
            schema is null ? [] : [("@s", schema)]).ConfigureAwait(false);
    }
}
