namespace Bengine.Introspection.Tree;

using System.Data.Common;
using Bengine.Enums;
using Bengine.Introspection.Model;
using Bengine.Introspection.Providers;

/// <summary>
/// The SQL Server object tree: a server holds Databases and Logins; a database
/// holds Schemas; a schema holds Tables, Views, Procedures, Functions,
/// Sequences, and Types; a table holds Columns, Constraints, Indexes, and
/// Triggers. Module SQL comes from <c>OBJECT_DEFINITION</c>; tables are rendered
/// by Bengine's generator from the introspected AST.
/// </summary>
public sealed class SqlServerObjectTree : ObjectTreeBase
{
    private const string UserSchemas = "s.name NOT IN ('sys','INFORMATION_SCHEMA','guest') AND s.schema_id < 16384";
    private const string ObjectId = "OBJECT_ID(QUOTENAME(@s) + '.' + QUOTENAME(@r))";

    /// <summary>Creates the tree over an open SQL Server connection.</summary>
    /// <param name="connection">An open SQL Server connection.</param>
    public SqlServerObjectTree(DbConnection connection)
        : base(connection, DatabaseProvider.SqlServer)
    {
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<TreeNode>> GetChildrenAsync(NodeRef node, CancellationToken cancellationToken = default) =>
        node.Kind switch
        {
            NodeKind.Server => ServerChildrenAsync(node, cancellationToken),
            NodeKind.DatabasesFolder => DatabasesAsync(node, cancellationToken),
            NodeKind.RolesFolder => NamesAsync(node, NodeKind.LoginRole, "SELECT name, type_desc AS detail FROM sys.server_principals WHERE type IN ('S','U','G') AND name NOT LIKE '##%' ORDER BY name", cancellationToken),
            NodeKind.Database => DatabaseChildrenAsync(node, cancellationToken),
            NodeKind.SchemasFolder => SchemasAsync(node, cancellationToken),
            NodeKind.Schema => SchemaChildrenAsync(node, cancellationToken),
            NodeKind.TablesFolder => RelationsAsync(node, NodeKind.Table, "sys.tables", cancellationToken),
            NodeKind.ViewsFolder => RelationsAsync(node, NodeKind.View, "sys.views", cancellationToken),
            NodeKind.ProceduresFolder => NamesAsync(node, NodeKind.Procedure, "SELECT o.name, NULL AS detail FROM sys.procedures o WHERE SCHEMA_NAME(o.schema_id) = @s ORDER BY o.name", cancellationToken, ("@s", node.Schema)),
            NodeKind.FunctionsFolder => NamesAsync(node, NodeKind.Function, "SELECT o.name, o.type_desc AS detail FROM sys.objects o WHERE SCHEMA_NAME(o.schema_id) = @s AND o.type IN ('FN','IF','TF','FS','FT') ORDER BY o.name", cancellationToken, ("@s", node.Schema)),
            NodeKind.SequencesFolder => NamesAsync(node, NodeKind.Sequence, "SELECT o.name, NULL AS detail FROM sys.sequences o WHERE SCHEMA_NAME(o.schema_id) = @s ORDER BY o.name", cancellationToken, ("@s", node.Schema)),
            NodeKind.TypesFolder => NamesAsync(node, NodeKind.Type, "SELECT t.name, NULL AS detail FROM sys.types t WHERE t.is_user_defined = 1 AND SCHEMA_NAME(t.schema_id) = @s ORDER BY t.name", cancellationToken, ("@s", node.Schema)),
            NodeKind.Table => RelationChildrenAsync(node, full: true, cancellationToken),
            NodeKind.View => RelationChildrenAsync(node, full: false, cancellationToken),
            NodeKind.ColumnsFolder => ColumnsAsync(node, cancellationToken),
            NodeKind.ConstraintsFolder => ConstraintsAsync(node, cancellationToken),
            NodeKind.IndexesFolder => IndexesAsync(node, cancellationToken),
            NodeKind.TriggersFolder => NamesAsync(node, NodeKind.Trigger, $"SELECT name, CASE WHEN is_disabled = 1 THEN 'disabled' END AS detail FROM sys.triggers WHERE parent_id = {ObjectId} ORDER BY name", cancellationToken, ("@s", node.Schema), ("@r", node.Relation)),
            _ => Task.FromResult<IReadOnlyList<TreeNode>>([]),
        };

    /// <inheritdoc />
    public override Task<ObjectProperties> GetPropertiesAsync(NodeRef node, CancellationToken cancellationToken = default) =>
        node.Kind switch
        {
            NodeKind.Server => ServerPropertiesAsync(cancellationToken),
            NodeKind.Database => DatabasePropertiesAsync(node, cancellationToken),
            NodeKind.Table => TablePropertiesAsync(node, cancellationToken),
            NodeKind.View or NodeKind.Procedure or NodeKind.Function => ModulePropertiesAsync(node, $"{Q(node.Schema!)}.{Q(node.Name!)}", cancellationToken),
            NodeKind.Trigger => ModulePropertiesAsync(node, $"{Q(node.Schema!)}.{Q(node.Name!)}", cancellationToken),
            NodeKind.Column => ColumnPropertiesAsync(node, cancellationToken),
            _ => Task.FromResult(new ObjectProperties { Title = node.Name ?? node.Kind.ToString() }),
        };

    private static string Q(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private async Task<IReadOnlyList<TreeNode>> ServerChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var counts = await RowAsync(
            "SELECT (SELECT count(*) FROM sys.databases WHERE database_id > 4) AS databases, (SELECT count(*) FROM sys.server_principals WHERE type IN ('S','U','G') AND name NOT LIKE '##%') AS logins",
            ct).ConfigureAwait(false);
        return
        [
            Folder(node, NodeKind.DatabasesFolder, "Databases", Long(counts, "databases")),
            Folder(node, NodeKind.RolesFolder, "Logins", Long(counts, "logins")),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> DatabasesAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            "SELECT name, state_desc FROM sys.databases WHERE database_id > 4 ORDER BY name",
            reader =>
            {
                var name = Str(reader, "name");
                var online = Str(reader, "state_desc") == "ONLINE";
                return new TreeNode
                {
                    Ref = folder.Item(NodeKind.Database, name) with { Database = name },
                    Label = name,
                    Detail = online ? null : Str(reader, "state_desc").ToLowerInvariant(),
                    HasChildren = online,
                };
            },
            ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> DatabaseChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var schemas = await TryScalarAsync($"SELECT count(*) FROM sys.schemas s WHERE {UserSchemas}", ct).ConfigureAwait(false);
        return [Folder(node with { Database = node.Name }, NodeKind.SchemasFolder, "Schemas", schemas is null ? null : Convert.ToInt64(schemas, System.Globalization.CultureInfo.InvariantCulture))];
    }

    private async Task<IReadOnlyList<TreeNode>> SchemasAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            $"SELECT s.name FROM sys.schemas s WHERE {UserSchemas} ORDER BY s.name",
            reader =>
            {
                var name = Str(reader, "name");
                return new TreeNode { Ref = folder.Item(NodeKind.Schema, name) with { Schema = name }, Label = name, HasChildren = true };
            },
            ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> SchemaChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var counts = await RowAsync(
            """
            SELECT (SELECT count(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = @s) AS tables,
                   (SELECT count(*) FROM sys.views WHERE SCHEMA_NAME(schema_id) = @s) AS views,
                   (SELECT count(*) FROM sys.procedures WHERE SCHEMA_NAME(schema_id) = @s) AS procedures,
                   (SELECT count(*) FROM sys.objects WHERE SCHEMA_NAME(schema_id) = @s AND type IN ('FN','IF','TF','FS','FT')) AS functions,
                   (SELECT count(*) FROM sys.sequences WHERE SCHEMA_NAME(schema_id) = @s) AS sequences,
                   (SELECT count(*) FROM sys.types WHERE is_user_defined = 1 AND SCHEMA_NAME(schema_id) = @s) AS types
            """,
            ct,
            ("@s", node.Schema)).ConfigureAwait(false);

        return
        [
            Folder(node, NodeKind.TablesFolder, "Tables", Long(counts, "tables")),
            Folder(node, NodeKind.ViewsFolder, "Views", Long(counts, "views")),
            Folder(node, NodeKind.ProceduresFolder, "Stored Procedures", Long(counts, "procedures")),
            Folder(node, NodeKind.FunctionsFolder, "Functions", Long(counts, "functions")),
            Folder(node, NodeKind.SequencesFolder, "Sequences", Long(counts, "sequences")),
            Folder(node, NodeKind.TypesFolder, "Types", Long(counts, "types")),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> RelationsAsync(NodeRef folder, NodeKind kind, string catalog, CancellationToken ct) =>
        await QueryAsync(
            $"SELECT o.name FROM {catalog} o WHERE SCHEMA_NAME(o.schema_id) = @s ORDER BY o.name",
            reader =>
            {
                var name = Str(reader, "name");
                return new TreeNode { Ref = folder.Item(kind, name) with { Relation = name }, Label = name, HasChildren = true };
            },
            ct,
            ("@s", folder.Schema)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> RelationChildrenAsync(NodeRef node, bool full, CancellationToken ct)
    {
        var counts = await RowAsync(
            $"""
            SELECT (SELECT count(*) FROM sys.columns WHERE object_id = {ObjectId}) AS columns,
                   (SELECT count(*) FROM sys.key_constraints WHERE parent_object_id = {ObjectId})
                     + (SELECT count(*) FROM sys.foreign_keys WHERE parent_object_id = {ObjectId})
                     + (SELECT count(*) FROM sys.check_constraints WHERE parent_object_id = {ObjectId}) AS constraints,
                   (SELECT count(*) FROM sys.indexes WHERE object_id = {ObjectId} AND type > 0 AND is_primary_key = 0 AND is_unique_constraint = 0) AS indexes,
                   (SELECT count(*) FROM sys.triggers WHERE parent_id = {ObjectId}) AS triggers
            """,
            ct,
            ("@s", node.Schema),
            ("@r", node.Relation)).ConfigureAwait(false);

        if (!full)
        {
            return [Folder(node, NodeKind.ColumnsFolder, "Columns", Long(counts, "columns"))];
        }

        return
        [
            Folder(node, NodeKind.ColumnsFolder, "Columns", Long(counts, "columns")),
            Folder(node, NodeKind.ConstraintsFolder, "Constraints", Long(counts, "constraints")),
            Folder(node, NodeKind.IndexesFolder, "Indexes", Long(counts, "indexes")),
            Folder(node, NodeKind.TriggersFolder, "Triggers", Long(counts, "triggers")),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> ColumnsAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            $"""
            SELECT c.name, TYPE_NAME(c.user_type_id) AS type, c.max_length, c.precision, c.scale,
                   CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                                     WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END AS pk
            FROM sys.columns c WHERE c.object_id = {ObjectId} ORDER BY c.column_id
            """,
            reader => Item(folder, NodeKind.Column, Str(reader, "name"), detail: Str(reader, "type"), badge: Bool(reader, "pk") ? "PK" : null),
            ct,
            ("@s", folder.Schema),
            ("@r", folder.Relation)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> ConstraintsAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            $"""
            SELECT name, type FROM sys.key_constraints WHERE parent_object_id = {ObjectId}
            UNION ALL SELECT name, 'F' FROM sys.foreign_keys WHERE parent_object_id = {ObjectId}
            UNION ALL SELECT name, 'C' FROM sys.check_constraints WHERE parent_object_id = {ObjectId}
            ORDER BY type, name
            """,
            reader => Item(
                folder,
                Str(reader, "type").Trim() switch
                {
                    "PK" => NodeKind.PrimaryKey,
                    "UQ" => NodeKind.UniqueConstraint,
                    "F" => NodeKind.ForeignKey,
                    _ => NodeKind.CheckConstraint,
                },
                Str(reader, "name")),
            ct,
            ("@s", folder.Schema),
            ("@r", folder.Relation)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> IndexesAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            $"SELECT name, is_unique, type_desc FROM sys.indexes WHERE object_id = {ObjectId} AND type > 0 AND is_primary_key = 0 AND is_unique_constraint = 0 ORDER BY name",
            reader => Item(folder, NodeKind.Index, Str(reader, "name"), detail: (Bool(reader, "is_unique") ? "unique " : string.Empty) + Str(reader, "type_desc").ToLowerInvariant()),
            ct,
            ("@s", folder.Schema),
            ("@r", folder.Relation)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> NamesAsync(
        NodeRef folder,
        NodeKind kind,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters) =>
        await QueryAsync(
            sql,
            reader => Item(folder, kind, Str(reader, "name"), detail: StrOrNull(reader, "detail")),
            ct,
            parameters).ConfigureAwait(false);

    private async Task<ObjectProperties> ServerPropertiesAsync(CancellationToken ct)
    {
        var row = await RowAsync(
            "SELECT @@VERSION AS version, SUSER_SNAME() AS login, @@SERVERNAME AS server, SERVERPROPERTY('Edition') AS edition, SERVERPROPERTY('Collation') AS collation",
            ct).ConfigureAwait(false);
        return new ObjectProperties
        {
            Title = "Server",
            Properties = Props(("Server", Get(row, "server")), ("Version", Get(row, "version")), ("Edition", Get(row, "edition")), ("Collation", Get(row, "collation")), ("Connected as", Get(row, "login"))),
        };
    }

    private async Task<ObjectProperties> DatabasePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            "SELECT SUSER_SNAME(owner_sid) AS owner, collation_name, recovery_model_desc, state_desc, compatibility_level, create_date FROM sys.databases WHERE name = @n",
            ct,
            ("@n", node.Name)).ConfigureAwait(false);
        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(("Name", node.Name), ("Owner", Get(row, "owner")), ("State", Get(row, "state_desc")), ("Collation", Get(row, "collation_name")), ("Recovery model", Get(row, "recovery_model_desc")), ("Compatibility level", Get(row, "compatibility_level")), ("Created", Get(row, "create_date"))),
            Sql = $"CREATE DATABASE {Q(node.Name!)}\n    COLLATE {Get(row, "collation_name")};",
        };
    }

    private async Task<ObjectProperties> TablePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT SUM(p.rows) AS rows, t.create_date, t.modify_date
            FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
            WHERE t.object_id = OBJECT_ID(QUOTENAME(@s) + '.' + QUOTENAME(@n))
            GROUP BY t.create_date, t.modify_date
            """,
            ct,
            ("@s", node.Schema),
            ("@n", node.Name)).ConfigureAwait(false);

        string ddl;
        try
        {
            var table = await new SqlServerSchemaProvider(Connection).IntrospectTableAsync(new TableRef(node.Name!, node.Schema), ct).ConfigureAwait(false);
            ddl = SchemaDdlRenderer.RenderTable(DatabaseProvider.SqlServer, table);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ddl = $"-- The table DDL could not be generated: {ex.Message}";
        }

        return new ObjectProperties
        {
            Title = $"{node.Schema}.{node.Name}",
            Properties = Props(("Name", node.Name), ("Schema", node.Schema), ("Rows", Get(row, "rows")), ("Created", Get(row, "create_date")), ("Modified", Get(row, "modify_date"))),
            Sql = ddl,
        };
    }

    private async Task<ObjectProperties> ModulePropertiesAsync(NodeRef node, string qualified, CancellationToken ct)
    {
        var row = await RowAsync(
            "SELECT OBJECT_DEFINITION(OBJECT_ID(@q)) AS definition, o.type_desc, o.create_date, o.modify_date FROM sys.objects o WHERE o.object_id = OBJECT_ID(@q)",
            ct,
            ("@q", qualified)).ConfigureAwait(false);
        return new ObjectProperties
        {
            Title = $"{node.Schema}.{node.Name}",
            Properties = Props(("Name", node.Name), ("Schema", node.Schema), ("Type", Get(row, "type_desc")), ("Created", Get(row, "create_date")), ("Modified", Get(row, "modify_date"))),
            Sql = Get(row, "definition"),
        };
    }

    private async Task<ObjectProperties> ColumnPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            $"""
            SELECT c.column_id, TYPE_NAME(c.user_type_id) AS type, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
                   c.is_computed, OBJECT_DEFINITION(c.default_object_id) AS dflt, c.collation_name
            FROM sys.columns c WHERE c.object_id = {ObjectId} AND c.name = @n
            """,
            ct,
            ("@s", node.Schema),
            ("@r", node.Relation),
            ("@n", node.Name)).ConfigureAwait(false);
        return new ObjectProperties
        {
            Title = $"{node.Relation}.{node.Name}",
            Properties = Props(
                ("Name", node.Name),
                ("Position", Get(row, "column_id")),
                ("Type", Get(row, "type")),
                ("Max length", Get(row, "max_length")),
                ("Precision", Get(row, "precision")),
                ("Scale", Get(row, "scale")),
                ("Nullable", Get(row, "is_nullable")),
                ("Identity", Get(row, "is_identity")),
                ("Computed", Get(row, "is_computed")),
                ("Default", Get(row, "dflt")),
                ("Collation", Get(row, "collation_name"))),
        };
    }
}
