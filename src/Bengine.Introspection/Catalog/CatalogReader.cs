namespace Bengine.Introspection.Catalog;

using System.Data.Common;
using System.Globalization;
using Bengine.Enums;
using Bengine.Introspection.Model;

/// <summary>
/// Reads a <see cref="DatabaseCatalog"/> with one bulk query per object kind
/// (columns of every relation, foreign keys, routines) rather than one query per
/// table, so a database with hundreds of tables loads in a few round trips.
/// System schemas are excluded. MySQL reads only the connected database, which
/// is MySQL's schema.
/// </summary>
public static class CatalogReader
{
    private const string PgUserSchemas =
        "n.nspname NOT IN ('information_schema') AND n.nspname NOT LIKE 'pg\\_%'";

    /// <summary>Reads the catalog of the connection's current database.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="provider">The provider.</param>
    /// <param name="transaction">The connection's open transaction, if any.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="IntrospectionException">A catalog query failed.</exception>
    public static async Task<DatabaseCatalog> ReadAsync(
        DbConnection connection,
        DatabaseProvider provider,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var reader = new Reader(connection, transaction, cancellationToken);
            return provider switch
            {
                DatabaseProvider.PostgreSql => await ReadPostgresAsync(reader, connection).ConfigureAwait(false),
                DatabaseProvider.MySql => await ReadMySqlAsync(reader).ConfigureAwait(false),
                DatabaseProvider.SqlServer => await ReadSqlServerAsync(reader, connection).ConfigureAwait(false),
                DatabaseProvider.Sqlite => await ReadSqliteAsync(reader).ConfigureAwait(false),
                DatabaseProvider.Oracle => await ReadOracleAsync(reader).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IntrospectionException)
        {
            throw new IntrospectionException($"Could not read the {provider} catalog. {ex.Message}", ex);
        }
    }

    private static async Task<DatabaseCatalog> ReadPostgresAsync(Reader reader, DbConnection connection)
    {
        var defaultSchema = await reader.ScalarAsync("SELECT current_schema()").ConfigureAwait(false);
        var schemas = await reader.ListAsync(
            $"SELECT n.nspname FROM pg_namespace n WHERE {PgUserSchemas} ORDER BY 1",
            r => r.GetString(0)).ConfigureAwait(false);

        var columns = await reader.ListAsync($"""
            SELECT n.nspname, c.relname, c.relkind::text, a.attname, format_type(a.atttypid, a.atttypmod),
                   NOT a.attnotnull, coalesce(pk.is_pk, false), (a.atthasdef OR a.attidentity <> ''), a.attgenerated <> ''
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            LEFT JOIN LATERAL (
                SELECT true AS is_pk FROM pg_index i
                WHERE i.indrelid = c.oid AND i.indisprimary AND a.attnum = ANY (i.indkey)
            ) pk ON true
            WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f') AND {PgUserSchemas}
            ORDER BY n.nspname, c.relname, a.attnum
            """,
            r => new Row(r.GetString(0), r.GetString(1), RelationKind(r.GetString(2)),
                new CatalogColumn(r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7), r.GetBoolean(8)))).ConfigureAwait(false);

        var keys = await reader.ListAsync($"""
            SELECT con.conname, n.nspname, c.relname, fn.nspname, fc.relname,
                   array_to_string(ARRAY(SELECT a.attname FROM unnest(con.conkey) WITH ORDINALITY k(n, o)
                       JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.n ORDER BY k.o), chr(31)),
                   array_to_string(ARRAY(SELECT a.attname FROM unnest(con.confkey) WITH ORDINALITY k(n, o)
                       JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.n ORDER BY k.o), chr(31))
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_class fc ON fc.oid = con.confrelid JOIN pg_namespace fn ON fn.oid = fc.relnamespace
            WHERE con.contype = 'f' AND {PgUserSchemas}
            """,
            r => new CatalogForeignKey(r.GetString(0), r.GetString(1), r.GetString(2), Split(r.GetString(5)),
                r.GetString(3), r.GetString(4), Split(r.GetString(6)))).ConfigureAwait(false);

        var routines = await reader.ListAsync($"""
            SELECT DISTINCT n.nspname, p.proname, p.prokind = 'p'
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE {PgUserSchemas} AND p.prokind IN ('f', 'p')
            """,
            r => new CatalogRoutine(r.GetString(0), r.GetString(1), r.GetBoolean(2))).ConfigureAwait(false);

        return new DatabaseCatalog(DatabaseProvider.PostgreSql, connection.Database, defaultSchema, schemas, Group(columns), keys, routines);
    }

    private static async Task<DatabaseCatalog> ReadMySqlAsync(Reader reader)
    {
        var database = await reader.ScalarAsync("SELECT DATABASE()").ConfigureAwait(false);
        const string scope = "IN (SELECT DATABASE())";
        var schemas = database is null ? [] : new List<string> { database };

        var columns = await reader.ListAsync($"""
            SELECT c.table_schema, c.table_name, t.table_type, c.column_name, c.column_type,
                   c.is_nullable = 'YES', c.column_key = 'PRI',
                   (c.column_default IS NOT NULL OR c.extra LIKE '%auto_increment%'), c.extra LIKE '%GENERATED%'
            FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE c.table_schema {scope}
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """,
            r => new Row(r.GetString(0), r.GetString(1), r.GetString(2) == "VIEW" ? TableKind.View : TableKind.Table,
                new CatalogColumn(r.GetString(3), r.GetString(4), Bool(r, 5), Bool(r, 6), Bool(r, 7), Bool(r, 8)))).ConfigureAwait(false);

        var keyRows = await reader.ListAsync($"""
            SELECT constraint_name, table_schema, table_name, column_name,
                   referenced_table_schema, referenced_table_name, referenced_column_name
            FROM information_schema.key_column_usage
            WHERE table_schema {scope} AND referenced_table_name IS NOT NULL
            ORDER BY table_schema, table_name, constraint_name, ordinal_position
            """,
            r => (Name: r.GetString(0), Schema: r.GetString(1), Table: r.GetString(2), Column: r.GetString(3),
                RefSchema: r.GetString(4), RefTable: r.GetString(5), RefColumn: r.GetString(6))).ConfigureAwait(false);

        var keys = keyRows
            .GroupBy(k => (k.Schema, k.Table, k.Name))
            .Select(g => new CatalogForeignKey(g.Key.Name, g.Key.Schema, g.Key.Table, g.Select(k => k.Column).ToList(),
                g.First().RefSchema, g.First().RefTable, g.Select(k => k.RefColumn).ToList()))
            .ToList();

        var routines = await reader.ListAsync($"""
            SELECT routine_schema, routine_name, routine_type = 'PROCEDURE'
            FROM information_schema.routines WHERE routine_schema {scope}
            """,
            r => new CatalogRoutine(r.GetString(0), r.GetString(1), Bool(r, 2))).ConfigureAwait(false);

        return new DatabaseCatalog(DatabaseProvider.MySql, database ?? string.Empty, database, schemas, Group(columns), keys, routines);
    }

    private static async Task<DatabaseCatalog> ReadSqlServerAsync(Reader reader, DbConnection connection)
    {
        var defaultSchema = await reader.ScalarAsync("SELECT SCHEMA_NAME()").ConfigureAwait(false);
        const string userSchemas = "s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest') AND s.name NOT LIKE 'db[_]%'";
        var schemas = await reader.ListAsync(
            $"SELECT s.name FROM sys.schemas s WHERE {userSchemas} ORDER BY s.name",
            r => r.GetString(0)).ConfigureAwait(false);

        var columns = await reader.ListAsync($"""
            SELECT s.name, o.name, o.type, c.name,
                   TYPE_NAME(c.user_type_id) + CASE
                       WHEN TYPE_NAME(c.user_type_id) IN ('varchar', 'char', 'varbinary', 'binary')
                           THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                       WHEN TYPE_NAME(c.user_type_id) IN ('nvarchar', 'nchar')
                           THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                       WHEN TYPE_NAME(c.user_type_id) IN ('decimal', 'numeric')
                           THEN '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
                       ELSE '' END,
                   CAST(c.is_nullable AS bit),
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i
                       ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                       WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END AS bit),
                   CAST(CASE WHEN c.default_object_id <> 0 OR c.is_identity = 1 THEN 1 ELSE 0 END AS bit),
                   CAST(c.is_computed AS bit)
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 AND {userSchemas}
            ORDER BY s.name, o.name, c.column_id
            """,
            r => new Row(r.GetString(0), r.GetString(1), r.GetString(2).Trim() == "V" ? TableKind.View : TableKind.Table,
                new CatalogColumn(r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7), r.GetBoolean(8)))).ConfigureAwait(false);

        var keyRows = await reader.ListAsync("""
            SELECT fk.name, s.name, t.name, pc.name, rs.name, rt.name, rc.name
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns k ON k.constraint_object_id = fk.object_id
            JOIN sys.tables t ON t.object_id = fk.parent_object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns pc ON pc.object_id = k.parent_object_id AND pc.column_id = k.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = k.referenced_object_id AND rc.column_id = k.referenced_column_id
            ORDER BY s.name, t.name, fk.name, k.constraint_column_id
            """,
            r => (Name: r.GetString(0), Schema: r.GetString(1), Table: r.GetString(2), Column: r.GetString(3),
                RefSchema: r.GetString(4), RefTable: r.GetString(5), RefColumn: r.GetString(6))).ConfigureAwait(false);

        var keys = keyRows
            .GroupBy(k => (k.Schema, k.Table, k.Name))
            .Select(g => new CatalogForeignKey(g.Key.Name, g.Key.Schema, g.Key.Table, g.Select(k => k.Column).ToList(),
                g.First().RefSchema, g.First().RefTable, g.Select(k => k.RefColumn).ToList()))
            .ToList();

        var routines = await reader.ListAsync($"""
            SELECT s.name, o.name, CAST(CASE WHEN o.type = 'P' THEN 1 ELSE 0 END AS bit)
            FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0 AND {userSchemas}
            """,
            r => new CatalogRoutine(r.GetString(0), r.GetString(1), r.GetBoolean(2))).ConfigureAwait(false);

        return new DatabaseCatalog(DatabaseProvider.SqlServer, connection.Database, defaultSchema, schemas, Group(columns), keys, routines);
    }

    private static async Task<DatabaseCatalog> ReadSqliteAsync(Reader reader)
    {
        var columns = await reader.ListAsync("""
            SELECT m.name, m.type, p.name, p.type, p."notnull", p.pk, p.dflt_value IS NOT NULL, p.hidden
            FROM sqlite_master m JOIN pragma_table_xinfo(m.name) p
            WHERE m.type IN ('table', 'view') AND m.name NOT LIKE 'sqlite\_%' ESCAPE '\'
            ORDER BY m.name, p.cid
            """,
            r => new Row(null, r.GetString(0), r.GetString(1) == "view" ? TableKind.View : TableKind.Table,
                new CatalogColumn(r.GetString(2), r.GetString(3), r.GetInt64(4) == 0, r.GetInt64(5) > 0,
                    r.GetBoolean(6) || (r.GetInt64(5) == 1 && r.GetString(3).Equals("INTEGER", StringComparison.OrdinalIgnoreCase)),
                    r.GetInt64(7) is 2 or 3))).ConfigureAwait(false);

        var keyRows = await reader.ListAsync("""
            SELECT m.name, f.id, f."table", f."from", f."to"
            FROM sqlite_master m JOIN pragma_foreign_key_list(m.name) f
            WHERE m.type = 'table'
            ORDER BY m.name, f.id, f.seq
            """,
            r => (Table: r.GetString(0), Id: r.GetInt64(1), RefTable: r.GetString(2), Column: r.GetString(3),
                RefColumn: r.IsDBNull(4) ? null : r.GetString(4))).ConfigureAwait(false);

        var keys = new List<CatalogForeignKey>();
        foreach (var group in keyRows.GroupBy(k => (k.Table, k.Id)))
        {
            var refTable = group.First().RefTable;

            // REFERENCES t without a column list targets t's primary key.
            var refColumns = group.All(k => k.RefColumn is not null)
                ? group.Select(k => k.RefColumn!).ToList()
                : columns.Where(c => c.Table.Equals(refTable, StringComparison.OrdinalIgnoreCase) && c.Column.IsPrimaryKey).Select(c => c.Column.Name).ToList();
            keys.Add(new CatalogForeignKey(
                $"fk_{group.Key.Table}_{group.Key.Id.ToString(CultureInfo.InvariantCulture)}",
                null, group.Key.Table, group.Select(k => k.Column).ToList(), null, refTable, refColumns));
        }

        return new DatabaseCatalog(DatabaseProvider.Sqlite, "main", null, [], Group(columns), keys, []);
    }

    // Oracle: the user schemas the connected user can see, plus its own.
    private static async Task<DatabaseCatalog> ReadOracleAsync(Reader reader)
    {
        const string Users = "SELECT username FROM all_users WHERE oracle_maintained = 'N' OR username = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')";
        var current = await reader.ScalarAsync("SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM dual").ConfigureAwait(false);
        var service = await reader.ScalarAsync("SELECT SYS_CONTEXT('USERENV', 'SERVICE_NAME') FROM dual").ConfigureAwait(false);
        var schemas = await reader.ListAsync(Users + " ORDER BY 1", r => r.GetString(0)).ConfigureAwait(false);

        var columns = await reader.ListAsync($"""
            SELECT c.owner, c.table_name, CASE WHEN v.view_name IS NULL AND m.mview_name IS NULL THEN 'T' ELSE 'V' END, c.column_name,
                   c.data_type || CASE
                       WHEN c.data_type IN ('VARCHAR2', 'NVARCHAR2', 'CHAR', 'NCHAR') THEN '(' || c.char_length || ')'
                       WHEN c.data_type = 'RAW' THEN '(' || c.data_length || ')'
                       WHEN c.data_type = 'NUMBER' AND c.data_precision IS NOT NULL
                           THEN '(' || c.data_precision || CASE WHEN c.data_scale > 0 THEN ',' || c.data_scale END || ')' END,
                   CASE c.nullable WHEN 'Y' THEN 1 ELSE 0 END,
                   CASE WHEN pk.column_name IS NULL THEN 0 ELSE 1 END,
                   CASE WHEN c.default_length > 0 OR c.identity_column = 'YES' THEN 1 ELSE 0 END,
                   CASE c.virtual_column WHEN 'YES' THEN 1 ELSE 0 END
            FROM all_tab_cols c
            JOIN all_objects o ON o.owner = c.owner AND o.object_name = c.table_name AND o.object_type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW')
            LEFT JOIN all_views v ON v.owner = c.owner AND v.view_name = c.table_name
            LEFT JOIN all_mviews m ON m.owner = c.owner AND m.mview_name = c.table_name
            LEFT JOIN (SELECT DISTINCT cc.owner, cc.table_name, cc.column_name FROM all_constraints k
                       JOIN all_cons_columns cc ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name
                       WHERE k.constraint_type = 'P') pk
              ON pk.owner = c.owner AND pk.table_name = c.table_name AND pk.column_name = c.column_name
            WHERE c.owner IN ({Users}) AND c.hidden_column = 'NO' AND c.table_name NOT LIKE 'BIN$%'
              AND (o.object_type <> 'TABLE' OR m.mview_name IS NULL)
            ORDER BY c.owner, c.table_name, c.column_id
            """,
            r => new Row(r.GetString(0), r.GetString(1), r.GetString(2) == "V" ? TableKind.View : TableKind.Table,
                new CatalogColumn(r.GetString(3), r.GetString(4), Bool(r, 5), Bool(r, 6), Bool(r, 7), Bool(r, 8)))).ConfigureAwait(false);

        var keyRows = await reader.ListAsync($"""
            SELECT c.constraint_name, c.owner, c.table_name, cc.column_name, r.owner, r.table_name, rc.column_name
            FROM all_constraints c
            JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
            JOIN all_constraints r ON r.owner = c.r_owner AND r.constraint_name = c.r_constraint_name
            JOIN all_cons_columns rc ON rc.owner = r.owner AND rc.constraint_name = r.constraint_name AND rc.position = cc.position
            WHERE c.constraint_type = 'R' AND c.owner IN ({Users})
            ORDER BY c.owner, c.table_name, c.constraint_name, cc.position
            """,
            r => (Name: r.GetString(0), Schema: r.GetString(1), Table: r.GetString(2), Column: r.GetString(3),
                RefSchema: r.GetString(4), RefTable: r.GetString(5), RefColumn: r.GetString(6))).ConfigureAwait(false);
        var keys = keyRows
            .GroupBy(k => (k.Schema, k.Table, k.Name))
            .Select(g => new CatalogForeignKey(g.Key.Name, g.Key.Schema, g.Key.Table, g.Select(k => k.Column).ToList(),
                g.First().RefSchema, g.First().RefTable, g.Select(k => k.RefColumn).ToList()))
            .ToList();

        var routines = await reader.ListAsync($"""
            SELECT owner, object_name, CASE object_type WHEN 'PROCEDURE' THEN 1 ELSE 0 END
            FROM all_objects WHERE object_type IN ('FUNCTION', 'PROCEDURE') AND owner IN ({Users})
            """,
            r => new CatalogRoutine(r.GetString(0), r.GetString(1), Bool(r, 2))).ConfigureAwait(false);

        return new DatabaseCatalog(DatabaseProvider.Oracle, service ?? string.Empty, current, schemas, Group(columns), keys, routines);
    }

    private static List<CatalogRelation> Group(List<Row> rows) => rows
        .GroupBy(r => (r.Schema, r.Table, r.Kind))
        .Select(g => new CatalogRelation(g.Key.Schema, g.Key.Table, g.Key.Kind, g.Select(r => r.Column).ToList()))
        .ToList();

    private static TableKind RelationKind(string relkind) => relkind switch
    {
        "v" => TableKind.View,
        "m" => TableKind.View,
        _ => TableKind.Table,
    };

    private static bool Bool(DbDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture) != 0;

    private static IReadOnlyList<string> Split(string value) => value.Split('\u001f');

    private sealed record Row(string? Schema, string Table, TableKind Kind, CatalogColumn Column);

    private sealed class Reader(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        public async Task<string?> ScalarAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            Connection.OracleSupport.Prepare(command);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? null : value.ToString();
        }

        public async Task<List<T>> ListAsync<T>(string sql, Func<DbDataReader, T> read)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            Connection.OracleSupport.Prepare(command);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            Connection.OracleSupport.Prepare(reader);
            var result = new List<T>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(read(reader));
            }

            return result;
        }
    }
}
