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
    protected override string NativeTypeSql => "column_type";

    /// <inheritdoc />
    protected override string ColumnCommentSql => "column_comment";

    /// <inheritdoc />
    protected override string ForeignKeySql => """
        SELECT kcu.constraint_name, kcu.column_name, kcu.ordinal_position,
               kcu.referenced_table_schema AS referenced_schema,
               kcu.referenced_table_name AS referenced_table,
               kcu.referenced_column_name AS referenced_column,
               rc.update_rule, rc.delete_rule
        FROM information_schema.key_column_usage kcu
        JOIN information_schema.referential_constraints rc
          ON rc.constraint_schema = kcu.constraint_schema
         AND rc.constraint_name = kcu.constraint_name
         AND rc.table_name = kcu.table_name
        WHERE kcu.referenced_table_name IS NOT NULL
          AND kcu.table_name = @t AND (@s IS NULL OR kcu.table_schema = @s)
        ORDER BY kcu.constraint_name, kcu.ordinal_position
        """;

    /// <summary>
    /// MySQL reports a literal string default bare (<c>anon</c>), while an
    /// expression default is flagged <c>DEFAULT_GENERATED</c>; quote the
    /// literals so they read like every other provider's. MariaDB already quotes.
    /// </summary>
    protected override string DefaultSql => """
        CASE WHEN column_default IS NULL OR VERSION() LIKE '%MariaDB%' OR extra LIKE '%DEFAULT_GENERATED%'
                  OR column_default REGEXP '^-?[0-9]+(\\.[0-9]+)?$' OR data_type IN ('bit')
             THEN column_default
             ELSE CONCAT('''', REPLACE(column_default, '''', ''''''), '''') END
        """;

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<CheckConstraint>> ReadChecksAsync(TableRef table, CancellationToken cancellationToken)
    {
        try
        {
            return await QueryAsync(
                """
                SELECT cc.constraint_name AS name, cc.check_clause AS clause
                FROM information_schema.check_constraints cc
                JOIN information_schema.table_constraints tc
                  ON tc.constraint_schema = cc.constraint_schema AND tc.constraint_name = cc.constraint_name
                WHERE tc.constraint_type = 'CHECK' AND tc.table_name = @t AND (@s IS NULL OR tc.table_schema = @s)
                ORDER BY cc.constraint_name
                """,
                reader => new CheckConstraint { Name = String(reader, "name"), Expression = Unwrap(String(reader, "clause")) },
                cancellationToken,
                ("@t", table.Name),
                ("@s", table.Schema)).ConfigureAwait(false);
        }
        catch (IntrospectionException)
        {
            // MySQL before 8.0.16 has no check_constraints view.
            return [];
        }
    }

    /// <inheritdoc />
    protected override async Task<string?> ReadTableCommentAsync(TableRef table, CancellationToken cancellationToken) =>
        (await QueryAsync(
            "SELECT table_comment AS c FROM information_schema.tables WHERE table_name = @t AND (@s IS NULL OR table_schema = @s)",
            reader => StringOrNull(reader, "c"),
            cancellationToken,
            ("@t", table.Name),
            ("@s", table.Schema)).ConfigureAwait(false)).FirstOrDefault();

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

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<View>> ReadViewsAsync(string? schema, CancellationToken cancellationToken)
    {
        var sql = "SELECT table_schema AS s, table_name AS n, view_definition AS d FROM information_schema.views WHERE "
            + (schema is null ? "table_schema = DATABASE()" : "table_schema = @s") + " ORDER BY table_name";
        return await QueryAsync(
            sql,
            reader =>
            {
                // MySQL qualifies every name with the view's database; drop it so the view moves with a dump.
                var owner = String(reader, "s");
                return new View
                {
                    Schema = owner,
                    Name = String(reader, "n"),
                    Definition = (StringOrNull(reader, "d") ?? string.Empty).Replace($"`{owner}`.", string.Empty, StringComparison.Ordinal),
                };
            },
            cancellationToken,
            schema is null ? [] : [("@s", schema)]).ConfigureAwait(false);
    }
}
