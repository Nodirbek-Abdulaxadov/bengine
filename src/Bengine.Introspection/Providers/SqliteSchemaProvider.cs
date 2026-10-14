namespace Bengine.Introspection.Providers;

using System.Data.Common;
using System.Globalization;
using Bengine.Enums;
using Bengine.Introspection.Model;
using Bengine.Models;
using Bengine.Models.Options;

/// <summary>
/// Discovers and introspects a SQLite database through its table-valued
/// <c>pragma_*</c> functions (<c>pragma_table_info</c>,
/// <c>pragma_foreign_key_list</c>, <c>pragma_index_list</c>,
/// <c>pragma_index_xinfo</c>). SQLite has no schemas, so
/// <see cref="GetSchemasAsync"/> is empty and every <see cref="TableRef"/> is
/// unqualified.
/// </summary>
public sealed class SqliteSchemaProvider : SchemaProviderBase
{
    /// <summary>Creates a provider over an open SQLite connection.</summary>
    /// <param name="connection">An open SQLite connection.</param>
    public SqliteSchemaProvider(DbConnection connection)
        : base(connection, DatabaseProvider.Sqlite)
    {
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default)
    {
        var names = await QueryAsync(
            "SELECT name FROM pragma_database_list ORDER BY seq",
            reader => String(reader, "name"),
            cancellationToken).ConfigureAwait(false);
        return names.Count == 0 ? ["main"] : names;
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<string>> GetSchemasAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public override async Task<IReadOnlyList<TableRef>> GetTablesAsync(
        string? schema = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT name, type
            FROM sqlite_master
            WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name
            """;

        return await QueryAsync(
            sql,
            reader =>
            {
                var kind = String(reader, "type") == "view" ? TableKind.View : TableKind.Table;
                return new TableRef(String(reader, "name"), Schema: null, kind);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<Table> IntrospectTableAsync(TableRef table, CancellationToken cancellationToken = default)
    {
        var columns = await ReadColumnsAsync(table.Name, cancellationToken).ConfigureAwait(false);
        var primaryKey = BuildPrimaryKey(table.Name, columns);
        var foreignKeys = await ReadForeignKeysAsync(table.Name, cancellationToken).ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(table.Name, cancellationToken).ConfigureAwait(false);
        var checks = await ReadChecksAsync(table.Name, cancellationToken).ConfigureAwait(false);

        return new Table
        {
            Name = table.Name,
            Schema = null,
            Columns = columns.Select(c => c.Column).ToList(),
            PrimaryKey = primaryKey,
            ForeignKeys = foreignKeys,
            Indexes = indexes,
            CheckConstraints = checks,
        };
    }

    // SQLite keeps no catalog of check constraints; read them back from the table's DDL.
    private async Task<IReadOnlyList<CheckConstraint>> ReadChecksAsync(string tableName, CancellationToken cancellationToken)
    {
        var ddl = (await QueryAsync(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @t",
            reader => StringOrNull(reader, "sql"),
            cancellationToken,
            ("@t", tableName)).ConfigureAwait(false)).FirstOrDefault();
        if (ddl is null || !ddl.Contains("CHECK", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        try
        {
            return Bengine.Parsing.SqlSchemaParser.ParseAs(ddl, DatabaseProvider.Sqlite).Tables.FirstOrDefault()?.CheckConstraints ?? [];
        }
        catch (Bengine.Parsing.SqlParseException)
        {
            return [];
        }
    }

    private sealed record ColumnInfo(Column Column, int PrimaryKeyPosition, bool IsIntegerType);

    private async Task<List<ColumnInfo>> ReadColumnsAsync(string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT name, type, "notnull", dflt_value, pk
            FROM pragma_table_info(@t)
            ORDER BY cid
            """;

        return await QueryAsync(
            sql,
            reader =>
            {
                var name = String(reader, "name");
                var nativeType = String(reader, "type");
                var notNull = IntOrNull(reader, "notnull") == 1;
                var defaultText = StringOrNull(reader, "dflt_value");
                var pkPosition = IntOrNull(reader, "pk") ?? 0;

                var (baseName, length, precision, scale) = SplitNativeType(nativeType);
                var (typeOptions, _) = NativeTypeMapper.Map(
                    DatabaseProvider.Sqlite, baseName, length, precision, scale);

                var column = new Column
                {
                    Name = name,
                    Type = typeOptions,
                    Nullability = notNull ? Nullability.NotNull : Nullability.Nullable,
                    Default = ParseDefault(defaultText),
                    ProviderOptions = NativeType(DatabaseProvider.Sqlite, nativeType),
                };

                var isInteger = typeOptions.DataType
                    is DbDataType.Integer or DbDataType.BigInt
                    or DbDataType.SmallInt or DbDataType.TinyInt;

                return new ColumnInfo(column, pkPosition, isInteger);
            },
            cancellationToken,
            ("@t", tableName)).ConfigureAwait(false);
    }

    private static PrimaryKey? BuildPrimaryKey(string tableName, List<ColumnInfo> columns)
    {
        var members = columns
            .Where(c => c.PrimaryKeyPosition > 0)
            .OrderBy(c => c.PrimaryKeyPosition)
            .ToList();

        if (members.Count == 0)
        {
            return null;
        }

        // A single-column INTEGER primary key is SQLite's rowid alias and
        // auto-populates, so model it as auto-increment on that column.
        if (members is [{ IsIntegerType: true } only])
        {
            MarkAutoIncrement(columns, only.Column.Name);
        }

        return new PrimaryKey
        {
            Name = $"pk_{tableName}",
            ColumnNames = members.Select(m => m.Column.Name).ToList(),
        };
    }

    private static void MarkAutoIncrement(List<ColumnInfo> columns, string columnName)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (!columns[i].Column.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var updated = WithIdentity(
                columns[i].Column,
                new IdentityOptions { Strategy = IdentityStrategy.AutoIncrement });
            columns[i] = columns[i] with { Column = updated };
            return;
        }
    }

    private async Task<List<ForeignKey>> ReadForeignKeysAsync(string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, seq, "table", "from", "to", on_update, on_delete
            FROM pragma_foreign_key_list(@t)
            ORDER BY id, seq
            """;

        var rows = await QueryAsync(
            sql,
            reader => new
            {
                Id = IntOrNull(reader, "id") ?? 0,
                Seq = IntOrNull(reader, "seq") ?? 0,
                Table = String(reader, "table"),
                From = String(reader, "from"),
                To = String(reader, "to"),
                OnUpdate = String(reader, "on_update"),
                OnDelete = String(reader, "on_delete"),
            },
            cancellationToken,
            ("@t", tableName)).ConfigureAwait(false);

        return rows
            .GroupBy(r => r.Id)
            .Select(group =>
            {
                var ordered = group.OrderBy(r => r.Seq).ToList();
                var first = ordered[0];
                return new ForeignKey
                {
                    Name = $"fk_{tableName}_{first.Table}_{first.Id}",
                    Columns = ordered.Select(r => r.From).ToList(),
                    ReferencedTable = first.Table,
                    ReferencedColumns = ordered.Select(r => r.To).ToList(),
                    OnDelete = ParseAction(first.OnDelete),
                    OnUpdate = ParseAction(first.OnUpdate),
                };
            })
            .ToList();
    }

    private async Task<List<Index>> ReadIndexesAsync(string tableName, CancellationToken cancellationToken)
    {
        const string listSql = """
            SELECT name, "unique", origin
            FROM pragma_index_list(@t)
            ORDER BY seq
            """;

        var indexRows = await QueryAsync(
            listSql,
            reader => new
            {
                Name = String(reader, "name"),
                Unique = IntOrNull(reader, "unique") == 1,
                Origin = String(reader, "origin"),
            },
            cancellationToken,
            ("@t", tableName)).ConfigureAwait(false);

        var indexes = new List<Index>();
        foreach (var row in indexRows)
        {
            // 'pk' indexes back the primary key, already modeled on the table.
            if (row.Origin == "pk")
            {
                continue;
            }

            const string infoSql = """
                SELECT name, desc
                FROM pragma_index_xinfo(@idx)
                WHERE key = 1
                ORDER BY seqno
                """;

            var columns = await QueryAsync(
                infoSql,
                reader =>
                {
                    var name = StringOrNull(reader, "name");
                    var descending = IntOrNull(reader, "desc") == 1;
                    return name is null
                        ? null
                        : new IndexColumn
                        {
                            ColumnName = name,
                            Direction = descending ? SortDirection.Descending : SortDirection.Ascending,
                        };
                },
                cancellationToken,
                ("@idx", row.Name)).ConfigureAwait(false);

            var keyColumns = columns.Where(c => c is not null).Select(c => c!).ToList();
            if (keyColumns.Count == 0)
            {
                continue;
            }

            indexes.Add(new Index
            {
                // A UNIQUE constraint's index has a reserved name no statement may use; name it after its columns.
                Name = row.Name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal)
                    ? $"uq_{tableName}_{string.Join('_', keyColumns.Select(c => c.ColumnName))}"
                    : row.Name,
                Type = row.Unique ? IndexType.Unique : IndexType.NonUnique,
                Columns = keyColumns,
            });
        }

        return indexes;
    }

    private static DefaultValue ParseDefault(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultValue.None;
        }

        var trimmed = text.Trim();
        if (trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultValue.None;
        }

        if (trimmed.Equals("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultValue.CurrentTimestamp;
        }

        if (trimmed.Equals("CURRENT_DATE", StringComparison.OrdinalIgnoreCase))
        {
            return new DefaultValue { Kind = DefaultValueKind.CurrentDate };
        }

        if (trimmed.Equals("CURRENT_TIME", StringComparison.OrdinalIgnoreCase))
        {
            return new DefaultValue { Kind = DefaultValueKind.CurrentTime };
        }

        // A string or numeric literal is stored as-is (strings keep their quotes);
        // anything parenthesized is a provider expression.
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
        {
            return DefaultValue.Expression(trimmed);
        }

        return DefaultValue.Literal(trimmed);
    }

    private static ReferentialAction ParseAction(string action) => action.ToUpperInvariant() switch
    {
        "CASCADE" => ReferentialAction.Cascade,
        "SET NULL" => ReferentialAction.SetNull,
        "SET DEFAULT" => ReferentialAction.SetDefault,
        "RESTRICT" => ReferentialAction.Restrict,
        _ => ReferentialAction.NoAction,
    };

    // Splits "VARCHAR(50)" / "DECIMAL(10,2)" into base name and numeric arguments.
    private static (string BaseName, int? Length, int? Precision, int? Scale) SplitNativeType(string nativeType)
    {
        if (string.IsNullOrWhiteSpace(nativeType))
        {
            return ("blob", null, null, null);
        }

        var open = nativeType.IndexOf('(');
        if (open < 0)
        {
            return (nativeType.Trim(), null, null, null);
        }

        var baseName = nativeType[..open].Trim();
        var close = nativeType.IndexOf(')', open);
        if (close < 0)
        {
            return (baseName, null, null, null);
        }

        var args = nativeType[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        int? first = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : null;
        int? second = args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : null;

        // One argument is a length or precision; two are precision and scale.
        return second is null
            ? (baseName, first, first, null)
            : (baseName, null, first, second);
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<View>> ReadViewsAsync(string? schema, CancellationToken cancellationToken) =>
        await QueryAsync(
            "SELECT name, sql FROM sqlite_master WHERE type = 'view' ORDER BY name",
            reader => new View { Name = String(reader, "name"), Definition = ViewBody(StringOrNull(reader, "sql") ?? string.Empty) },
            cancellationToken).ConfigureAwait(false);
}
