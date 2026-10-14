namespace Bengine.Introspection.Providers;

using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Bengine.Enums;
using Bengine.Introspection.Model;
using Bengine.Models;
using Bengine.Models.Options;

/// <summary>
/// Oracle introspection over the <c>ALL_*</c> dictionary views, which show what
/// the connected user may see. A "database" is the service connected to and a
/// schema is a user; without a schema the current one is read. Columns keep
/// their exact native spelling (<c>NUMBER(10,2)</c>, <c>VARCHAR2(100 CHAR)</c>,
/// <c>TIMESTAMP(6) WITH TIME ZONE</c>) beside the logical type; identity
/// columns, virtual columns, comments, keys, unique constraints, indexes, and
/// checks are read (the <c>NOT NULL</c> checks Oracle keeps as constraints are
/// left to nullability).
/// </summary>
public sealed partial class OracleSchemaProvider : SchemaProviderBase
{
    /// <summary>Creates a provider over an open Oracle connection.</summary>
    /// <param name="connection">An open connection.</param>
    public OracleSchemaProvider(DbConnection connection)
        : base(connection, DatabaseProvider.Oracle)
    {
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default) =>
        await QueryAsync(
            "SELECT SYS_CONTEXT('USERENV', 'SERVICE_NAME') AS name FROM dual",
            reader => String(reader, "name"),
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> GetSchemasAsync(CancellationToken cancellationToken = default) =>
        await QueryAsync(
            """
            SELECT username AS name FROM all_users
            WHERE oracle_maintained = 'N' OR username = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')
            ORDER BY username
            """,
            reader => String(reader, "name"),
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override async Task<IReadOnlyList<TableRef>> GetTablesAsync(string? schema = null, CancellationToken cancellationToken = default)
    {
        var owner = await OwnerAsync(schema, cancellationToken).ConfigureAwait(false);
        return await QueryAsync(
            """
            SELECT table_name AS name, 'T' AS kind FROM all_tables
            WHERE owner = :s AND nested = 'NO' AND secondary = 'N' AND (iot_type IS NULL OR iot_type = 'IOT')
              AND table_name NOT LIKE 'BIN$%'
              AND table_name NOT IN (SELECT mview_name FROM all_mviews WHERE owner = :s)
            UNION ALL SELECT view_name, 'V' FROM all_views WHERE owner = :s
            UNION ALL SELECT mview_name, 'M' FROM all_mviews WHERE owner = :s
            ORDER BY 2, 1
            """,
            reader => new TableRef(String(reader, "name"), owner, String(reader, "kind") == "T" ? TableKind.Table : TableKind.View),
            cancellationToken,
            (":s", owner)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<Table> IntrospectTableAsync(TableRef table, CancellationToken cancellationToken = default)
    {
        var owner = await OwnerAsync(table.Schema, cancellationToken).ConfigureAwait(false);
        var columns = await ReadColumnsAsync(owner, table.Name, cancellationToken).ConfigureAwait(false);
        var constraints = await QueryAsync(
            """
            SELECT c.constraint_name AS name, c.constraint_type AS type, cc.column_name AS col, cc.position AS pos,
                   c.delete_rule AS on_delete, c.search_condition_vc AS cond, c.generated AS generated,
                   r.owner AS r_owner, r.table_name AS r_table, rc.column_name AS r_col
            FROM all_constraints c
            LEFT JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name AND cc.table_name = c.table_name
            LEFT JOIN all_constraints r ON r.owner = c.r_owner AND r.constraint_name = c.r_constraint_name
            LEFT JOIN all_cons_columns rc ON rc.owner = r.owner AND rc.constraint_name = r.constraint_name AND rc.position = cc.position
            WHERE c.owner = :s AND c.table_name = :t AND c.constraint_type IN ('P', 'U', 'R', 'C')
            ORDER BY c.constraint_name, cc.position
            """,
            reader => new ConstraintRow(
                String(reader, "name"), String(reader, "type"), StringOrNull(reader, "col"), StringOrNull(reader, "on_delete"),
                StringOrNull(reader, "cond"), StringOrNull(reader, "generated") == "GENERATED NAME",
                StringOrNull(reader, "r_owner"), StringOrNull(reader, "r_table"), StringOrNull(reader, "r_col")),
            cancellationToken,
            (":s", owner),
            (":t", table.Name)).ConfigureAwait(false);

        var primary = constraints.Where(c => c.Type == "P").ToList();
        var foreignKeys = constraints.Where(c => c.Type == "R").GroupBy(c => c.Name).Select(g => new ForeignKey
        {
            Name = g.Key,
            Columns = g.Select(c => c.Column!).ToList(),
            ReferencedSchema = g.First().RefOwner == owner ? null : g.First().RefOwner,
            ReferencedTable = g.First().RefTable!,
            ReferencedColumns = g.Select(c => c.RefColumn!).ToList(),
            OnDelete = g.First().OnDelete switch
            {
                "CASCADE" => ReferentialAction.Cascade,
                "SET NULL" => ReferentialAction.SetNull,
                _ => ReferentialAction.NoAction,
            },
            OnUpdate = ReferentialAction.NoAction,
        }).ToList();
        var checks = constraints
            .Where(c => c.Type == "C" && c.Condition is { } condition && !(c.SystemNamed && NotNullCheck().IsMatch(condition.Trim())))
            .GroupBy(c => c.Name)
            .Select(g => new CheckConstraint { Name = g.Key, Expression = g.First().Condition!.Trim() })
            .ToList();
        var uniques = constraints.Where(c => c.Type == "U").GroupBy(c => c.Name).Select(g => new Index
        {
            Name = g.Key,
            Type = IndexType.Unique,
            Columns = g.Select(c => IndexColumn.Ascending(c.Column!)).ToList(),
        });

        // Indexes the server made for keys, LOB indexes, and expression indexes are not modeled.
        var indexRows = await QueryAsync(
            """
            SELECT i.index_name AS name, i.uniqueness AS uniq, ic.column_name AS col, ic.descend AS dir
            FROM all_indexes i JOIN all_ind_columns ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name
            WHERE i.table_owner = :s AND i.table_name = :t AND i.index_type IN ('NORMAL', 'BITMAP')
              AND NOT EXISTS (SELECT 1 FROM all_constraints k WHERE k.owner = i.table_owner AND k.table_name = i.table_name AND k.index_name = i.index_name)
            ORDER BY i.index_name, ic.column_position
            """,
            reader => new IndexRow(String(reader, "name"), String(reader, "uniq") == "UNIQUE", String(reader, "col"), String(reader, "dir") == "DESC"),
            cancellationToken,
            (":s", owner),
            (":t", table.Name)).ConfigureAwait(false);

        var comment = (await QueryAsync(
            "SELECT comments AS c FROM all_tab_comments WHERE owner = :s AND table_name = :t",
            reader => StringOrNull(reader, "c"),
            cancellationToken,
            (":s", owner),
            (":t", table.Name)).ConfigureAwait(false)).FirstOrDefault();

        return new Table
        {
            Name = table.Name,
            Schema = owner,
            Columns = columns,
            PrimaryKey = primary.Count == 0 ? null : new PrimaryKey { Name = primary[0].Name, ColumnNames = primary.Select(c => c.Column!).ToList() },
            ForeignKeys = foreignKeys,
            Indexes = uniques.Concat(IndexRow.Group(indexRows)).ToList(),
            CheckConstraints = checks,
            Comment = string.IsNullOrEmpty(comment) ? null : comment,
        };
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<View>> ReadViewsAsync(string? schema, CancellationToken cancellationToken)
    {
        var owner = await OwnerAsync(schema, cancellationToken).ConfigureAwait(false);
        return await QueryAsync(
            """
            SELECT view_name AS name, text AS def, 0 AS mat FROM all_views WHERE owner = :s
            UNION ALL SELECT mview_name, query, 1 FROM all_mviews WHERE owner = :s
            """,
            reader => new View
            {
                Schema = owner,
                Name = String(reader, "name"),
                Definition = String(reader, "def").Trim().TrimEnd(';'),
                IsMaterialized = IntOrNull(reader, "mat") == 1,
            },
            cancellationToken,
            (":s", owner)).ConfigureAwait(false);
    }

    /// <summary>Maps an Oracle column type to the logical type and its exact native spelling.</summary>
    /// <param name="dataType">ALL_TAB_COLS.DATA_TYPE.</param>
    /// <param name="charLength">The length in characters.</param>
    /// <param name="charUsed">'C' for character semantics, 'B' for bytes.</param>
    /// <param name="dataLength">The length in bytes.</param>
    /// <param name="precision">The numeric precision.</param>
    /// <param name="scale">The numeric scale.</param>
    /// <returns>The logical type and native name.</returns>
    internal static (ColumnTypeOptions Type, string Native) MapType(string dataType, int? charLength, string? charUsed, int? dataLength, int? precision, int? scale)
    {
        var type = dataType.ToUpperInvariant();
        string Chars(string name) => $"{name}({charLength}{(charUsed == "C" && name is "VARCHAR2" or "CHAR" ? " CHAR" : string.Empty)})";
        return type switch
        {
            "NUMBER" when precision is null && scale is null => (new() { DataType = DbDataType.Decimal }, "NUMBER"),
            "NUMBER" when precision is null => (new() { DataType = DbDataType.Decimal, Precision = 38, Scale = scale }, $"NUMBER(*,{scale})"),
            "NUMBER" when scale is null or 0 && precision <= 19 => (new()
            {
                DataType = precision switch
                {
                    1 => DbDataType.Boolean,
                    <= 3 => DbDataType.TinyInt,
                    <= 5 => DbDataType.SmallInt,
                    <= 10 => DbDataType.Integer,
                    _ => DbDataType.BigInt,
                },
            }, $"NUMBER({precision})"),
            "NUMBER" => (new() { DataType = DbDataType.Decimal, Precision = precision, Scale = scale ?? 0 }, $"NUMBER({precision},{scale ?? 0})"),
            "FLOAT" => (new() { DataType = DbDataType.Double }, precision is { } p ? $"FLOAT({p})" : "FLOAT"),
            "BINARY_FLOAT" => (new() { DataType = DbDataType.Real }, type),
            "BINARY_DOUBLE" => (new() { DataType = DbDataType.Double }, type),
            "VARCHAR2" or "NVARCHAR2" => (new() { DataType = DbDataType.VarChar, Length = charLength }, Chars(type)),
            "CHAR" or "NCHAR" => (new() { DataType = DbDataType.Char, Length = charLength }, Chars(type)),
            "CLOB" or "NCLOB" or "LONG" => (new() { DataType = DbDataType.Text }, type),
            "RAW" when dataLength == 16 => (new() { DataType = DbDataType.Uuid }, "RAW(16)"),
            "RAW" => (new() { DataType = DbDataType.VarBinary, Length = dataLength }, $"RAW({dataLength})"),
            "BLOB" or "LONG RAW" or "BFILE" => (new() { DataType = DbDataType.Blob }, type),
            "DATE" => (new() { DataType = DbDataType.Timestamp }, type),
            "XMLTYPE" => (new() { DataType = DbDataType.Xml }, type),
            "JSON" => (new() { DataType = DbDataType.Json }, type),
            "BOOLEAN" => (new() { DataType = DbDataType.Boolean }, type),
            _ when type.StartsWith("TIMESTAMP", StringComparison.Ordinal) =>
                (new() { DataType = type.Contains("TIME ZONE", StringComparison.Ordinal) ? DbDataType.TimestampTz : DbDataType.Timestamp }, type),
            _ when type.StartsWith("INTERVAL DAY", StringComparison.Ordinal) => (new() { DataType = DbDataType.Time }, type),
            _ => (new() { DataType = DbDataType.Text }, type),
        };
    }

    /// <summary>Reads an Oracle column default (<c>DATA_DEFAULT</c>) into the AST.</summary>
    /// <param name="text">The default's SQL text.</param>
    /// <returns>The default.</returns>
    internal static DefaultValue ParseOracleDefault(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultValue.None;
        }

        if (trimmed.ToUpperInvariant() is "SYSDATE" or "SYSTIMESTAMP" or "CURRENT_TIMESTAMP" or "LOCALTIMESTAMP"
            || trimmed.StartsWith("CURRENT_TIMESTAMP(", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultValue.CurrentTimestamp;
        }

        if (trimmed.Equals("CURRENT_DATE", StringComparison.OrdinalIgnoreCase))
        {
            return new DefaultValue { Kind = DefaultValueKind.CurrentDate };
        }

        var literal = (trimmed.StartsWith('\'') && trimmed.EndsWith('\''))
            || decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        return literal ? DefaultValue.Literal(trimmed) : DefaultValue.Expression(trimmed);
    }

    private async Task<List<Column>> ReadColumnsAsync(string owner, string table, CancellationToken cancellationToken) =>
        await QueryAsync(
            """
            SELECT c.column_name AS name, c.data_type AS type, c.char_length AS char_len, c.char_used AS char_used,
                   c.data_length AS data_len, c.data_precision AS prec, c.data_scale AS scale, c.nullable AS nullable,
                   c.data_default AS def, c.identity_column AS ident, c.virtual_column AS virt, cc.comments AS comm
            FROM all_tab_cols c
            LEFT JOIN all_col_comments cc ON cc.owner = c.owner AND cc.table_name = c.table_name AND cc.column_name = c.column_name
            WHERE c.owner = :s AND c.table_name = :t AND c.hidden_column = 'NO'
            ORDER BY c.column_id
            """,
            reader =>
            {
                var (type, native) = MapType(String(reader, "type"), IntOrNull(reader, "char_len"), StringOrNull(reader, "char_used"),
                    IntOrNull(reader, "data_len"), IntOrNull(reader, "prec"), IntOrNull(reader, "scale"));
                var identity = StringOrNull(reader, "ident") == "YES";
                var isVirtual = StringOrNull(reader, "virt") == "YES";
                var defaultText = StringOrNull(reader, "def")?.Trim();
                return new Column
                {
                    Name = String(reader, "name"),
                    Type = type,
                    Nullability = String(reader, "nullable") == "Y" ? Nullability.Nullable : Nullability.NotNull,
                    Default = identity || isVirtual ? DefaultValue.None : ParseOracleDefault(defaultText),
                    Identity = identity ? new IdentityOptions { Strategy = IdentityStrategy.Identity } : IdentityOptions.None,
                    Generated = isVirtual && defaultText is { Length: > 0 }
                        ? new GeneratedColumn { Kind = GeneratedKind.Virtual, Expression = defaultText }
                        : null,
                    Comment = StringOrNull(reader, "comm") is { Length: > 0 } comment ? comment : null,
                    ProviderOptions = NativeType(DatabaseProvider.Oracle, native),
                };
            },
            cancellationToken,
            (":s", owner),
            (":t", table)).ConfigureAwait(false);

    // The schema to read: the one asked for, else the session's current schema.
    private async Task<string> OwnerAsync(string? schema, CancellationToken cancellationToken) =>
        schema ?? (await QueryAsync(
            "SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') AS s FROM dual",
            reader => String(reader, "s"),
            cancellationToken).ConfigureAwait(false))[0];

    [GeneratedRegex("^\"[^\"]+\" IS NOT NULL$")]
    private static partial Regex NotNullCheck();

    private sealed record ConstraintRow(
        string Name,
        string Type,
        string? Column,
        string? OnDelete,
        string? Condition,
        bool SystemNamed,
        string? RefOwner,
        string? RefTable,
        string? RefColumn);
}
