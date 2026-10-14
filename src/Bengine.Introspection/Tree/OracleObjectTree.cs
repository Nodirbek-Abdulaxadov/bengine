namespace Bengine.Introspection.Tree;

using System.Data.Common;
using System.Text;
using Bengine.Enums;
using Bengine.Introspection.Providers;

/// <summary>
/// The Oracle object tree, over the <c>ALL_*</c> dictionary views (what the
/// connected user may see): the service connected to as the one database, its
/// schemas (users; Oracle-maintained ones under Catalogs), and in each schema
/// tables, views, materialized views, sequences, functions, procedures,
/// packages, types, and synonyms; tables hold columns, constraints, indexes,
/// and triggers. DDL comes from <c>DBMS_METADATA</c>, without storage clauses.
/// </summary>
public sealed class OracleObjectTree : ObjectTreeBase
{
    private const string Relations =
        "nested = 'NO' AND secondary = 'N' AND (iot_type IS NULL OR iot_type = 'IOT') AND table_name NOT LIKE 'BIN$%'";

    /// <summary>Creates the tree over an open Oracle connection.</summary>
    /// <param name="connection">An open connection.</param>
    public OracleObjectTree(DbConnection connection)
        : base(connection, DatabaseProvider.Oracle)
    {
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<TreeNode>> GetChildrenAsync(NodeRef node, CancellationToken cancellationToken = default) =>
        node.Kind switch
        {
            NodeKind.Server => ServerChildrenAsync(node, cancellationToken),
            NodeKind.DatabasesFolder => DatabasesAsync(node, cancellationToken),
            NodeKind.RolesFolder => NamesAsync(node, NodeKind.LoginRole, "SELECT username AS name, NULL AS detail FROM all_users ORDER BY username", cancellationToken),
            NodeKind.TablespacesFolder => NamesAsync(node, NodeKind.Tablespace, "SELECT tablespace_name AS name, LOWER(contents) AS detail FROM user_tablespaces ORDER BY 1", cancellationToken),
            NodeKind.Database => DatabaseChildrenAsync(node, cancellationToken),
            NodeKind.SchemasFolder => SchemasAsync(node, NodeKind.Schema, "oracle_maintained = 'N' OR username = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')", cancellationToken),
            NodeKind.CatalogsFolder => SchemasAsync(node, NodeKind.Catalog, "oracle_maintained = 'Y' AND username <> SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')", cancellationToken),
            NodeKind.Schema or NodeKind.Catalog => SchemaChildrenAsync(node, cancellationToken),
            NodeKind.TablesFolder => RelationsAsync(node, NodeKind.Table, $"SELECT table_name AS name, NULL AS detail FROM all_tables WHERE owner = :s AND {Relations} AND table_name NOT IN (SELECT mview_name FROM all_mviews WHERE owner = :s) ORDER BY 1", cancellationToken),
            NodeKind.ViewsFolder => RelationsAsync(node, NodeKind.View, "SELECT view_name AS name, NULL AS detail FROM all_views WHERE owner = :s ORDER BY 1", cancellationToken),
            NodeKind.MaterializedViewsFolder => RelationsAsync(node, NodeKind.MaterializedView, "SELECT mview_name AS name, LOWER(refresh_method) AS detail FROM all_mviews WHERE owner = :s ORDER BY 1", cancellationToken),
            NodeKind.SequencesFolder => NamesAsync(node, NodeKind.Sequence, "SELECT sequence_name AS name, 'last ' || last_number AS detail FROM all_sequences WHERE sequence_owner = :s AND sequence_name NOT LIKE 'ISEQ$$%' ORDER BY 1", cancellationToken, (":s", node.Schema)),
            NodeKind.FunctionsFolder => ObjectsAsync(node, NodeKind.Function, "FUNCTION", cancellationToken),
            NodeKind.ProceduresFolder => ObjectsAsync(node, NodeKind.Procedure, "PROCEDURE", cancellationToken),
            NodeKind.PackagesFolder => ObjectsAsync(node, NodeKind.Package, "PACKAGE", cancellationToken),
            NodeKind.TypesFolder => ObjectsAsync(node, NodeKind.Type, "TYPE", cancellationToken),
            NodeKind.SynonymsFolder => NamesAsync(node, NodeKind.Synonym, "SELECT synonym_name AS name, table_owner || '.' || table_name AS detail FROM all_synonyms WHERE owner = :s ORDER BY 1", cancellationToken, (":s", node.Schema)),
            NodeKind.Table => RelationChildrenAsync(node, [NodeKind.ColumnsFolder, NodeKind.ConstraintsFolder, NodeKind.IndexesFolder, NodeKind.TriggersFolder], cancellationToken),
            NodeKind.View => RelationChildrenAsync(node, [NodeKind.ColumnsFolder, NodeKind.TriggersFolder], cancellationToken),
            NodeKind.MaterializedView => RelationChildrenAsync(node, [NodeKind.ColumnsFolder, NodeKind.IndexesFolder], cancellationToken),
            NodeKind.ColumnsFolder => ColumnsAsync(node, cancellationToken),
            NodeKind.ConstraintsFolder => ConstraintsAsync(node, cancellationToken),
            NodeKind.IndexesFolder => NamesAsync(node, NodeKind.Index, IndexesSql("index_name AS name, CASE uniqueness WHEN 'UNIQUE' THEN 'unique ' END || LOWER(index_type) AS detail") + " ORDER BY 1", cancellationToken, (":s", node.Schema), (":r", node.Relation)),
            NodeKind.TriggersFolder => NamesAsync(node, NodeKind.Trigger, "SELECT trigger_name AS name, LOWER(triggering_event) || CASE status WHEN 'DISABLED' THEN ' (disabled)' END AS detail FROM all_triggers WHERE table_owner = :s AND table_name = :r ORDER BY 1", cancellationToken, (":s", node.Schema), (":r", node.Relation)),
            _ => Task.FromResult<IReadOnlyList<TreeNode>>([]),
        };

    /// <inheritdoc />
    public override Task<ObjectProperties> GetPropertiesAsync(NodeRef node, CancellationToken cancellationToken = default) =>
        node.Kind switch
        {
            NodeKind.Server => ServerPropertiesAsync(cancellationToken),
            NodeKind.Database => DatabasePropertiesAsync(node, cancellationToken),
            NodeKind.Schema or NodeKind.Catalog => SchemaPropertiesAsync(node, cancellationToken),
            NodeKind.Table => TablePropertiesAsync(node, cancellationToken),
            NodeKind.Column => ColumnPropertiesAsync(node, cancellationToken),
            NodeKind.View => DdlAsync(node, "VIEW", node.Name!, cancellationToken),
            NodeKind.MaterializedView => DdlAsync(node, "MATERIALIZED_VIEW", node.Name!, cancellationToken),
            NodeKind.Sequence => SequencePropertiesAsync(node, cancellationToken),
            NodeKind.Function => DdlAsync(node, "FUNCTION", node.Name!, cancellationToken),
            NodeKind.Procedure => DdlAsync(node, "PROCEDURE", node.Name!, cancellationToken),
            NodeKind.Package => DdlAsync(node, "PACKAGE", node.Name!, cancellationToken),
            NodeKind.Type => DdlAsync(node, "TYPE", node.Name!, cancellationToken),
            NodeKind.Synonym => DdlAsync(node, "SYNONYM", node.Name!, cancellationToken),
            NodeKind.Trigger => DdlAsync(node, "TRIGGER", node.Name!, cancellationToken),
            NodeKind.Index => DdlAsync(node, "INDEX", node.Name!, cancellationToken),
            NodeKind.PrimaryKey or NodeKind.UniqueConstraint or NodeKind.CheckConstraint => DdlAsync(node, "CONSTRAINT", node.Name!, cancellationToken),
            NodeKind.ForeignKey => DdlAsync(node, "REF_CONSTRAINT", node.Name!, cancellationToken),
            NodeKind.LoginRole => DdlAsync(node with { Schema = null }, "USER", node.Name!, cancellationToken),
            NodeKind.Tablespace => DdlAsync(node with { Schema = null }, "TABLESPACE", node.Name!, cancellationToken),
            _ => Task.FromResult(new ObjectProperties { Title = node.Name ?? node.Kind.ToString() }),
        };

    // The indexes of a relation that no key or unique constraint created.
    private static string IndexesSql(string select) =>
        $"""
        SELECT {select} FROM all_indexes i
        WHERE i.table_owner = :s AND i.table_name = :r AND i.index_type <> 'LOB'
          AND NOT EXISTS (SELECT 1 FROM all_constraints k WHERE k.owner = i.table_owner AND k.table_name = i.table_name AND k.index_name = i.index_name)
        """;

    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private async Task<IReadOnlyList<TreeNode>> ServerChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var users = await TryScalarAsync("SELECT count(*) FROM all_users", ct).ConfigureAwait(false);
        var tablespaces = await TryScalarAsync("SELECT count(*) FROM user_tablespaces", ct).ConfigureAwait(false);
        return
        [
            Folder(node, NodeKind.DatabasesFolder, "Databases", 1),
            Folder(node, NodeKind.RolesFolder, "Users", ToLong(users)),
            Folder(node, NodeKind.TablespacesFolder, "Tablespaces", ToLong(tablespaces)),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> DatabasesAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            "SELECT SYS_CONTEXT('USERENV', 'SERVICE_NAME') AS name, SYS_CONTEXT('USERENV', 'CON_NAME') AS detail FROM dual",
            reader =>
            {
                var name = Str(reader, "name");
                return new TreeNode { Ref = folder.Item(NodeKind.Database, name) with { Database = name }, Label = name, Detail = StrOrNull(reader, "detail"), HasChildren = true };
            },
            ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> DatabaseChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var counts = await RowAsync(
            """
            SELECT SUM(CASE WHEN oracle_maintained = 'N' OR username = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') THEN 1 ELSE 0 END) AS schemas,
                   SUM(CASE WHEN oracle_maintained = 'Y' AND username <> SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') THEN 1 ELSE 0 END) AS catalogs
            FROM all_users
            """,
            ct).ConfigureAwait(false);
        return
        [
            Folder(node, NodeKind.SchemasFolder, "Schemas", Long(counts, "schemas")),
            Folder(node, NodeKind.CatalogsFolder, "Catalogs", Long(counts, "catalogs")),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> SchemasAsync(NodeRef folder, NodeKind kind, string where, CancellationToken ct) =>
        await QueryAsync(
            $"SELECT username AS name FROM all_users WHERE {where} ORDER BY username",
            reader =>
            {
                var name = Str(reader, "name");
                return new TreeNode { Ref = folder.Item(kind, name) with { Schema = name }, Label = name, HasChildren = true };
            },
            ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> SchemaChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var counts = (await QueryAsync(
            """
            SELECT object_type AS type, count(*) AS n FROM all_objects
            WHERE owner = :s AND object_type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW', 'SEQUENCE', 'FUNCTION', 'PROCEDURE', 'PACKAGE', 'TYPE', 'SYNONYM')
              AND object_name NOT LIKE 'BIN$%' AND object_name NOT LIKE 'ISEQ$$%'
            GROUP BY object_type
            """,
            reader => (Type: Str(reader, "type"), Count: LongOrNull(reader, "n") ?? 0),
            ct,
            (":s", node.Schema)).ConfigureAwait(false)).ToDictionary(r => r.Type, r => r.Count);
        long Count(string type) => counts.GetValueOrDefault(type);

        // A materialized view is also a table of the same name.
        return
        [
            Folder(node, NodeKind.TablesFolder, "Tables", Math.Max(0, Count("TABLE") - Count("MATERIALIZED VIEW"))),
            Folder(node, NodeKind.ViewsFolder, "Views", Count("VIEW")),
            Folder(node, NodeKind.MaterializedViewsFolder, "Materialized Views", Count("MATERIALIZED VIEW")),
            Folder(node, NodeKind.SequencesFolder, "Sequences", Count("SEQUENCE")),
            Folder(node, NodeKind.FunctionsFolder, "Functions", Count("FUNCTION")),
            Folder(node, NodeKind.ProceduresFolder, "Procedures", Count("PROCEDURE")),
            Folder(node, NodeKind.PackagesFolder, "Packages", Count("PACKAGE")),
            Folder(node, NodeKind.TypesFolder, "Types", Count("TYPE")),
            Folder(node, NodeKind.SynonymsFolder, "Synonyms", Count("SYNONYM")),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> RelationsAsync(NodeRef folder, NodeKind kind, string sql, CancellationToken ct) =>
        await QueryAsync(
            sql,
            reader =>
            {
                var name = Str(reader, "name");
                return new TreeNode { Ref = folder.Item(kind, name) with { Relation = name }, Label = name, Detail = StrOrNull(reader, "detail"), HasChildren = true };
            },
            ct,
            (":s", folder.Schema)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> ObjectsAsync(NodeRef folder, NodeKind kind, string type, CancellationToken ct) =>
        await NamesAsync(
            folder,
            kind,
            "SELECT object_name AS name, CASE status WHEN 'INVALID' THEN 'invalid' END AS detail FROM all_objects WHERE owner = :s AND object_type = :t ORDER BY 1",
            ct,
            (":s", folder.Schema),
            (":t", type)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> RelationChildrenAsync(NodeRef node, NodeKind[] folders, CancellationToken ct)
    {
        var counts = await RowAsync(
            $"""
            SELECT (SELECT count(*) FROM all_tab_cols WHERE owner = :s AND table_name = :r AND hidden_column = 'NO') AS columns,
                   (SELECT count(*) FROM all_constraints WHERE owner = :s AND table_name = :r AND constraint_type IN ('P', 'U', 'R', 'C')
                      AND NOT (generated = 'GENERATED NAME' AND constraint_type = 'C' AND search_condition_vc LIKE '"%" IS NOT NULL')) AS constraints,
                   (SELECT count(*) FROM ({IndexesSql("1 AS x")})) AS indexes,
                   (SELECT count(*) FROM all_triggers WHERE table_owner = :s AND table_name = :r) AS triggers
            FROM dual
            """,
            ct,
            (":s", node.Schema),
            (":r", node.Relation)).ConfigureAwait(false);
        return folders.Select(kind => kind switch
        {
            NodeKind.ColumnsFolder => Folder(node, kind, "Columns", Long(counts, "columns")),
            NodeKind.ConstraintsFolder => Folder(node, kind, "Constraints", Long(counts, "constraints")),
            NodeKind.IndexesFolder => Folder(node, kind, "Indexes", Long(counts, "indexes")),
            _ => Folder(node, kind, "Triggers", Long(counts, "triggers")),
        }).ToList();
    }

    private async Task<IReadOnlyList<TreeNode>> ColumnsAsync(NodeRef folder, CancellationToken ct)
    {
        var keys = (await QueryAsync(
            """
            SELECT cc.column_name AS name, c.constraint_type AS type FROM all_constraints c
            JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
            WHERE c.owner = :s AND c.table_name = :r AND c.constraint_type IN ('P', 'R')
            """,
            reader => (Name: Str(reader, "name"), Type: Str(reader, "type")),
            ct,
            (":s", folder.Schema),
            (":r", folder.Relation)).ConfigureAwait(false))
            .GroupBy(k => k.Name)
            .ToDictionary(g => g.Key, g => g.Any(k => k.Type == "P") ? "PK" : "FK");

        return await QueryAsync(
            """
            SELECT column_name AS name, data_type AS type, char_length AS char_len, char_used AS char_used, data_length AS data_len,
                   data_precision AS prec, data_scale AS scale, nullable AS nullable
            FROM all_tab_cols WHERE owner = :s AND table_name = :r AND hidden_column = 'NO' ORDER BY column_id
            """,
            reader =>
            {
                var name = Str(reader, "name");
                var native = NativeType(reader);
                return Item(folder, NodeKind.Column, name, detail: native.ToLowerInvariant() + (Str(reader, "nullable") == "N" ? " not null" : string.Empty), badge: keys.GetValueOrDefault(name));
            },
            ct,
            (":s", folder.Schema),
            (":r", folder.Relation)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TreeNode>> ConstraintsAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            """
            SELECT constraint_name AS name, constraint_type AS type, status AS status FROM all_constraints
            WHERE owner = :s AND table_name = :r AND constraint_type IN ('P', 'U', 'R', 'C')
              AND NOT (generated = 'GENERATED NAME' AND constraint_type = 'C' AND search_condition_vc LIKE '"%" IS NOT NULL')
            ORDER BY DECODE(constraint_type, 'P', 1, 'R', 2, 'U', 3, 4), constraint_name
            """,
            reader => Item(
                folder,
                Str(reader, "type") switch
                {
                    "P" => NodeKind.PrimaryKey,
                    "R" => NodeKind.ForeignKey,
                    "U" => NodeKind.UniqueConstraint,
                    _ => NodeKind.CheckConstraint,
                },
                Str(reader, "name"),
                detail: Str(reader, "status") == "DISABLED" ? "disabled" : null),
            ct,
            (":s", folder.Schema),
            (":r", folder.Relation)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> NamesAsync(NodeRef folder, NodeKind kind, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters) =>
        await QueryAsync(
            sql,
            reader => Item(folder, kind, Str(reader, "name"), detail: StrOrNull(reader, "detail")),
            ct,
            parameters).ConfigureAwait(false);

    private async Task<ObjectProperties> ServerPropertiesAsync(CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT (SELECT banner_full FROM v$version WHERE ROWNUM = 1) AS version, USER AS connected_as,
                   SYS_CONTEXT('USERENV', 'SERVER_HOST') AS host, SYS_CONTEXT('USERENV', 'DB_NAME') AS db,
                   SYS_CONTEXT('USERENV', 'SERVICE_NAME') AS service, SYS_CONTEXT('USERENV', 'CON_NAME') AS container
            FROM dual
            """,
            ct).ConfigureAwait(false);
        return new ObjectProperties
        {
            Title = "Server",
            Properties = Props(("Version", Get(row, "version")), ("Connected as", Get(row, "connected_as")), ("Host", Get(row, "host")),
                ("Database", Get(row, "db")), ("Service", Get(row, "service")), ("Container", Get(row, "container"))),
        };
    }

    private async Task<ObjectProperties> DatabasePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT SYS_CONTEXT('USERENV', 'DB_NAME') AS db, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') AS schema_name,
                   (SELECT value FROM nls_database_parameters WHERE parameter = 'NLS_CHARACTERSET') AS charset,
                   (SELECT value FROM nls_database_parameters WHERE parameter = 'NLS_NCHAR_CHARACTERSET') AS ncharset,
                   (SELECT version_full FROM product_component_version WHERE ROWNUM = 1) AS version
            FROM dual
            """,
            ct).ConfigureAwait(false);
        return new ObjectProperties
        {
            Title = node.Name ?? "Database",
            Properties = Props(("Service", node.Name), ("Database", Get(row, "db")), ("Current schema", Get(row, "schema_name")),
                ("Character set", Get(row, "charset")), ("National character set", Get(row, "ncharset")), ("Version", Get(row, "version"))),
        };
    }

    private async Task<ObjectProperties> SchemaPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            "SELECT username, created, oracle_maintained, common FROM all_users WHERE username = :n",
            ct,
            (":n", node.Name)).ConfigureAwait(false);
        var ddl = await GetDdlAsync("USER", node.Name!, null, ct).ConfigureAwait(false);
        return new ObjectProperties
        {
            Title = node.Name ?? "Schema",
            Properties = Props(("Name", node.Name), ("Created", Get(row, "created")), ("Oracle-maintained", Get(row, "oracle_maintained")), ("Common", Get(row, "common"))),
            Sql = ddl,
        };
    }

    private async Task<ObjectProperties> TablePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT t.tablespace_name, t.num_rows, t.last_analyzed, t.partitioned, t.temporary, t.logging,
                   (SELECT comments FROM all_tab_comments c WHERE c.owner = t.owner AND c.table_name = t.table_name) AS table_comment
            FROM all_tables t WHERE t.owner = :s AND t.table_name = :n
            """,
            ct,
            (":s", node.Schema),
            (":n", node.Name)).ConfigureAwait(false);

        // The table, then the indexes and comments that belong with it.
        var ddl = new StringBuilder(await GetDdlAsync("TABLE", node.Name!, node.Schema, ct).ConfigureAwait(false) ?? string.Empty);
        foreach (var dependent in new[] { "INDEX", "COMMENT" })
        {
            if (await GetDependentDdlAsync(dependent, node.Name!, node.Schema!, ct).ConfigureAwait(false) is { } more)
            {
                ddl.Append("\n\n").Append(more.Trim());
            }
        }

        return new ObjectProperties
        {
            Title = node.Name ?? "Table",
            Properties = Props(("Name", node.Name), ("Schema", node.Schema), ("Tablespace", Get(row, "tablespace_name")),
                ("Rows (last analyzed)", Get(row, "num_rows")), ("Last analyzed", Get(row, "last_analyzed")), ("Partitioned", Get(row, "partitioned")),
                ("Temporary", Get(row, "temporary")), ("Logging", Get(row, "logging")), ("Comment", Get(row, "table_comment"))),
            Sql = ddl.Length == 0 ? null : ddl.ToString(),
        };
    }

    private async Task<ObjectProperties> SequencePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            "SELECT min_value, max_value, increment_by, cycle_flag, cache_size, last_number FROM all_sequences WHERE sequence_owner = :s AND sequence_name = :n",
            ct,
            (":s", node.Schema),
            (":n", node.Name)).ConfigureAwait(false);
        return new ObjectProperties
        {
            Title = node.Name ?? "Sequence",
            Properties = Props(("Name", node.Name), ("Schema", node.Schema), ("Minimum", Get(row, "min_value")), ("Maximum", Get(row, "max_value")),
                ("Increment", Get(row, "increment_by")), ("Cycle", Get(row, "cycle_flag")), ("Cache", Get(row, "cache_size")), ("Last number", Get(row, "last_number"))),
            Sql = await GetDdlAsync("SEQUENCE", node.Name!, node.Schema, ct).ConfigureAwait(false),
        };
    }

    private async Task<ObjectProperties> ColumnPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var rows = await QueryAsync(
            """
            SELECT c.column_id AS position, c.data_type AS type, c.char_length AS char_len, c.char_used AS char_used, c.data_length AS data_len,
                   c.data_precision AS prec, c.data_scale AS scale, c.nullable AS nullable, c.data_default AS def,
                   c.identity_column AS ident, c.virtual_column AS virt, cc.comments AS comm
            FROM all_tab_cols c
            LEFT JOIN all_col_comments cc ON cc.owner = c.owner AND cc.table_name = c.table_name AND cc.column_name = c.column_name
            WHERE c.owner = :s AND c.table_name = :r AND c.column_name = :n
            """,
            reader => (Native: NativeType(reader), Position: StrOrNull(reader, "position"), Nullable: Str(reader, "nullable") == "Y",
                Default: StrOrNull(reader, "def")?.Trim(), Identity: Str(reader, "ident") == "YES", Virtual: Str(reader, "virt") == "YES", Comment: StrOrNull(reader, "comm")),
            ct,
            (":s", node.Schema),
            (":r", node.Relation),
            (":n", node.Name)).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return new ObjectProperties { Title = node.Name ?? "Column" };
        }

        var c = rows[0];
        var definition = $"{Q(node.Name!)} {c.Native}"
                         + (c.Virtual ? $" AS ({c.Default})" : c.Identity ? " GENERATED BY DEFAULT AS IDENTITY" : c.Default is { Length: > 0 } d ? $" DEFAULT {d}" : string.Empty)
                         + (c.Nullable ? string.Empty : " NOT NULL");
        return new ObjectProperties
        {
            Title = $"{node.Relation}.{node.Name}",
            Properties = Props(("Name", node.Name), ("Position", c.Position), ("Type", c.Native), ("Nullable", c.Nullable ? "Yes" : "No"),
                ("Default", c.Virtual || c.Identity ? null : c.Default), ("Identity", c.Identity ? "Yes" : null), ("Virtual", c.Virtual ? $"Yes: {c.Default}" : null), ("Comment", c.Comment)),
            Sql = $"ALTER TABLE {Q(node.Schema!)}.{Q(node.Relation!)}\n    MODIFY ({definition});",
        };
    }

    private async Task<ObjectProperties> DdlAsync(NodeRef node, string type, string name, CancellationToken ct) => new()
    {
        Title = name,
        Properties = Props(("Name", name), ("Schema", node.Schema), ("Type", type.Replace('_', ' ').ToLowerInvariant())),
        Sql = await GetDdlAsync(type, name, node.Schema, ct).ConfigureAwait(false),
    };

    // DBMS_METADATA DDL without storage and segment clauses, with SQL terminators; null when not allowed or not found.
    private async Task<string?> GetDdlAsync(string type, string name, string? schema, CancellationToken ct) =>
        await MetadataAsync(
            schema is null ? "SELECT DBMS_METADATA.GET_DDL(:t, :n) AS ddl FROM dual" : "SELECT DBMS_METADATA.GET_DDL(:t, :n, :s) AS ddl FROM dual",
            ct,
            schema is null ? [(":t", type), (":n", name)] : [(":t", type), (":n", name), (":s", schema)]).ConfigureAwait(false);

    private async Task<string?> GetDependentDdlAsync(string type, string table, string schema, CancellationToken ct) =>
        await MetadataAsync("SELECT DBMS_METADATA.GET_DEPENDENT_DDL(:t, :n, :s) AS ddl FROM dual", ct, (":t", type), (":n", table), (":s", schema)).ConfigureAwait(false);

    private async Task<string?> MetadataAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        try
        {
            await QueryAsync(
                """
                BEGIN
                  DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'SEGMENT_ATTRIBUTES', FALSE);
                  DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'STORAGE', FALSE);
                  DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'SQLTERMINATOR', TRUE);
                  DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'PRETTY', TRUE);
                END;
                """,
                _ => 0,
                ct).ConfigureAwait(false);
            var rows = await QueryAsync(sql, reader => StrOrNull(reader, "ddl"), ct, parameters).ConfigureAwait(false);
            return rows.FirstOrDefault()?.Trim() is { Length: > 0 } ddl ? ddl : null;
        }
        catch (IntrospectionException)
        {
            return null;
        }
    }

    private static string NativeType(DbDataReader reader)
    {
        int? Int(string column) => LongOrNull(reader, column) is { } v ? (int)v : null;
        return OracleSchemaProvider.MapType(Str(reader, "type"), Int("char_len"), StrOrNull(reader, "char_used"), Int("data_len"), Int("prec"), Int("scale")).Native;
    }

    private static long? ToLong(object? value) =>
        value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
}
