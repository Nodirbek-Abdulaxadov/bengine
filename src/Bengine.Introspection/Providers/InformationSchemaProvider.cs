namespace Bengine.Introspection.Providers;

using System.Data.Common;
using Bengine.Enums;
using Bengine.Introspection.Model;
using Bengine.Models;
using Bengine.Models.Options;

/// <summary>
/// Discovers and introspects the three server providers that expose the SQL
/// standard <c>information_schema</c> views — PostgreSQL, MySQL, and SQL Server —
/// through a shared implementation of tables, columns, primary keys, and foreign
/// keys. The parts the standard views do not cover uniformly — the schema list,
/// identity columns, and secondary indexes — are left to per-provider subclasses.
/// </summary>
public abstract class InformationSchemaProvider : SchemaProviderBase
{
    /// <summary>Creates a provider bound to an open connection.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="provider">The provider this instance speaks to.</param>
    protected InformationSchemaProvider(DbConnection connection, DatabaseProvider provider)
        : base(connection, provider)
    {
    }

    /// <summary>Gets the schema names the provider reserves for its own catalog, excluded from discovery.</summary>
    protected abstract IReadOnlySet<string> SystemSchemas { get; }

    /// <summary>
    /// Gets whether a schema is a system or session-temporary schema and so should
    /// be hidden from discovery. Covers the named system schemas and PostgreSQL's
    /// per-session <c>pg_temp_*</c> / <c>pg_toast_temp_*</c> and other <c>pg_*</c>
    /// schemas.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <returns><c>true</c> if the schema should be hidden.</returns>
    protected bool IsSystemSchema(string schema) =>
        SystemSchemas.Contains(schema) || schema.StartsWith("pg_", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> GetSchemasAsync(CancellationToken cancellationToken = default)
    {
        var all = await QueryAsync(
            "SELECT schema_name FROM information_schema.schemata ORDER BY schema_name",
            reader => String(reader, "schema_name"),
            cancellationToken).ConfigureAwait(false);

        return all.Where(s => !IsSystemSchema(s)).ToList();
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<TableRef>> GetTablesAsync(
        string? schema = null,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT table_schema, table_name, table_type
            FROM information_schema.tables
            WHERE table_type IN ('BASE TABLE', 'VIEW')
            """ + (schema is null ? string.Empty : " AND table_schema = @s") +
            " ORDER BY table_schema, table_name";

        var parameters = schema is null
            ? Array.Empty<(string, object?)>()
            : [("@s", (object?)schema)];

        var tables = await QueryAsync(
            sql,
            reader =>
            {
                var tableSchema = String(reader, "table_schema");
                var kind = String(reader, "table_type") == "VIEW" ? TableKind.View : TableKind.Table;
                return new TableRef(String(reader, "table_name"), tableSchema, kind);
            },
            cancellationToken,
            parameters).ConfigureAwait(false);

        return tables.Where(t => schema is not null || !IsSystemSchema(t.Schema ?? string.Empty)).ToList();
    }

    /// <inheritdoc />
    public override async Task<Table> IntrospectTableAsync(TableRef table, CancellationToken cancellationToken = default)
    {
        var columns = await ReadColumnsAsync(table, cancellationToken).ConfigureAwait(false);
        var identity = await ReadIdentityColumnsAsync(table, cancellationToken).ConfigureAwait(false);
        ApplyIdentity(columns, identity);

        var primaryKey = await ReadPrimaryKeyAsync(table, cancellationToken).ConfigureAwait(false);
        var foreignKeys = await ReadForeignKeysAsync(table, cancellationToken).ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(table, cancellationToken).ConfigureAwait(false);

        return new Table
        {
            Name = table.Name,
            Schema = table.Schema,
            Columns = columns,
            PrimaryKey = primaryKey,
            ForeignKeys = foreignKeys,
            Indexes = indexes,
        };
    }

    /// <summary>Reads the names of the relation's identity / auto-increment columns.</summary>
    /// <param name="table">The relation.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The identity column names, compared case-insensitively.</returns>
    protected abstract Task<IReadOnlySet<string>> ReadIdentityColumnsAsync(TableRef table, CancellationToken cancellationToken);

    /// <summary>Reads the relation's secondary indexes (excluding the primary-key index).</summary>
    /// <param name="table">The relation.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The indexes.</returns>
    protected abstract Task<IReadOnlyList<Index>> ReadIndexesAsync(TableRef table, CancellationToken cancellationToken);

    /// <summary>Classifies an <c>information_schema</c> column default into a <see cref="DefaultValue"/>.</summary>
    /// <param name="columnDefault">The raw default text, or <c>null</c>.</param>
    /// <returns>The classified default.</returns>
    protected virtual DefaultValue ParseDefault(string? columnDefault)
    {
        if (string.IsNullOrWhiteSpace(columnDefault))
        {
            return DefaultValue.None;
        }

        var trimmed = columnDefault.Trim();
        if (trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultValue.None;
        }

        if (trimmed.StartsWith("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("now()", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("getdate()", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultValue.CurrentTimestamp;
        }

        // A quoted string or bare number is a literal; anything else is an
        // expression the server evaluates.
        var isLiteral = (trimmed.StartsWith('\'') && trimmed.EndsWith('\''))
            || decimal.TryParse(trimmed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _);

        return isLiteral ? DefaultValue.Literal(trimmed) : DefaultValue.Expression(trimmed);
    }

    private async Task<List<Column>> ReadColumnsAsync(TableRef table, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT column_name, data_type, is_nullable, column_default,
                   character_maximum_length, numeric_precision, numeric_scale
            FROM information_schema.columns
            WHERE table_name = @t AND (@s IS NULL OR table_schema = @s)
            ORDER BY ordinal_position
            """;

        return await QueryAsync(
            sql,
            reader =>
            {
                var name = String(reader, "column_name");
                var dataType = String(reader, "data_type");
                var nullable = String(reader, "is_nullable").Equals("YES", StringComparison.OrdinalIgnoreCase);
                var defaultText = StringOrNull(reader, "column_default");
                var length = IntOrNull(reader, "character_maximum_length");
                var precision = IntOrNull(reader, "numeric_precision");
                var scale = IntOrNull(reader, "numeric_scale");

                var (typeOptions, _) = NativeTypeMapper.Map(Provider, dataType, length, precision, scale);

                return new Column
                {
                    Name = name,
                    Type = typeOptions,
                    Nullability = nullable ? Nullability.Nullable : Nullability.NotNull,
                    Default = ParseDefault(defaultText),
                };
            },
            cancellationToken,
            ("@t", table.Name),
            ("@s", table.Schema)).ConfigureAwait(false);
    }

    private async Task<PrimaryKey?> ReadPrimaryKeyAsync(TableRef table, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT kcu.column_name, kcu.ordinal_position, tc.constraint_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name
             AND tc.table_schema = kcu.table_schema
             AND tc.table_name = kcu.table_name
            WHERE tc.constraint_type = 'PRIMARY KEY'
              AND tc.table_name = @t AND (@s IS NULL OR tc.table_schema = @s)
            ORDER BY kcu.ordinal_position
            """;

        var rows = await QueryAsync(
            sql,
            reader => new
            {
                Column = String(reader, "column_name"),
                Name = String(reader, "constraint_name"),
            },
            cancellationToken,
            ("@t", table.Name),
            ("@s", table.Schema)).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return null;
        }

        return new PrimaryKey
        {
            Name = rows[0].Name,
            ColumnNames = rows.Select(r => r.Column).ToList(),
        };
    }

    private async Task<List<ForeignKey>> ReadForeignKeysAsync(TableRef table, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT rc.constraint_name,
                   kcu.column_name, kcu.ordinal_position,
                   ccu.table_schema AS referenced_schema,
                   ccu.table_name AS referenced_table,
                   ccu.column_name AS referenced_column,
                   rc.update_rule, rc.delete_rule
            FROM information_schema.referential_constraints rc
            JOIN information_schema.key_column_usage kcu
              ON rc.constraint_name = kcu.constraint_name
             AND rc.constraint_schema = kcu.constraint_schema
            JOIN information_schema.key_column_usage ccu
              ON rc.unique_constraint_name = ccu.constraint_name
             AND rc.unique_constraint_schema = ccu.constraint_schema
             AND kcu.ordinal_position = ccu.ordinal_position
            WHERE kcu.table_name = @t AND (@s IS NULL OR kcu.table_schema = @s)
            ORDER BY rc.constraint_name, kcu.ordinal_position
            """;

        var rows = await QueryAsync(
            sql,
            reader => new
            {
                Name = String(reader, "constraint_name"),
                Column = String(reader, "column_name"),
                ReferencedSchema = StringOrNull(reader, "referenced_schema"),
                ReferencedTable = String(reader, "referenced_table"),
                ReferencedColumn = String(reader, "referenced_column"),
                UpdateRule = String(reader, "update_rule"),
                DeleteRule = String(reader, "delete_rule"),
            },
            cancellationToken,
            ("@t", table.Name),
            ("@s", table.Schema)).ConfigureAwait(false);

        return rows
            .GroupBy(r => r.Name)
            .Select(group =>
            {
                var ordered = group.ToList();
                var first = ordered[0];
                return new ForeignKey
                {
                    Name = first.Name,
                    Columns = ordered.Select(r => r.Column).ToList(),
                    ReferencedTable = first.ReferencedTable,
                    ReferencedSchema = first.ReferencedSchema,
                    ReferencedColumns = ordered.Select(r => r.ReferencedColumn).ToList(),
                    OnUpdate = ParseAction(first.UpdateRule),
                    OnDelete = ParseAction(first.DeleteRule),
                };
            })
            .ToList();
    }

    private static void ApplyIdentity(List<Column> columns, IReadOnlySet<string> identityColumns)
    {
        if (identityColumns.Count == 0)
        {
            return;
        }

        for (var i = 0; i < columns.Count; i++)
        {
            if (identityColumns.Contains(columns[i].Name))
            {
                columns[i] = WithIdentity(
                    columns[i],
                    new IdentityOptions { Strategy = IdentityStrategy.Identity });
            }
        }
    }

    private static ReferentialAction ParseAction(string action) => action.ToUpperInvariant() switch
    {
        "CASCADE" => ReferentialAction.Cascade,
        "SET NULL" => ReferentialAction.SetNull,
        "SET DEFAULT" => ReferentialAction.SetDefault,
        "RESTRICT" => ReferentialAction.Restrict,
        _ => ReferentialAction.NoAction,
    };
}
