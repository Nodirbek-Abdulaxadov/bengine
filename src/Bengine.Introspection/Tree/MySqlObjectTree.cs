namespace Bengine.Introspection.Tree;

using System.Data.Common;
using Bengine.Enums;

/// <summary>
/// The MySQL object tree: a server holds Databases (MySQL schemas) and Users; a
/// database holds Tables, Views, Functions, Procedures, and Events; a table holds
/// Columns, Constraints, Indexes, and Triggers. Object SQL comes from MySQL's own
/// <c>SHOW CREATE …</c> statements. In MySQL a schema and a database are the same
/// thing, so database nodes set both <see cref="NodeRef.Database"/> and
/// <see cref="NodeRef.Schema"/>.
/// </summary>
public sealed class MySqlObjectTree : ObjectTreeBase
{
    private const string UserDatabases = "schema_name NOT IN ('information_schema','mysql','performance_schema','sys')";

    /// <summary>Creates the tree over an open MySQL connection.</summary>
    /// <param name="connection">An open MySQL connection.</param>
    public MySqlObjectTree(DbConnection connection)
        : base(connection, DatabaseProvider.MySql)
    {
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<TreeNode>> GetChildrenAsync(NodeRef node, CancellationToken cancellationToken = default) =>
        node.Kind switch
        {
            NodeKind.Server => ServerChildrenAsync(node, cancellationToken),
            NodeKind.DatabasesFolder => DatabasesAsync(node, cancellationToken),
            NodeKind.RolesFolder => UsersAsync(node, cancellationToken),
            NodeKind.Database => DatabaseChildrenAsync(node, cancellationToken),
            NodeKind.TablesFolder => RelationsAsync(node, NodeKind.Table, "BASE TABLE", cancellationToken),
            NodeKind.ViewsFolder => RelationsAsync(node, NodeKind.View, "VIEW", cancellationToken),
            NodeKind.FunctionsFolder => NamesAsync(node, NodeKind.Function, "SELECT routine_name AS name FROM information_schema.routines WHERE routine_schema = @s AND routine_type = 'FUNCTION' ORDER BY 1", cancellationToken),
            NodeKind.ProceduresFolder => NamesAsync(node, NodeKind.Procedure, "SELECT routine_name AS name FROM information_schema.routines WHERE routine_schema = @s AND routine_type = 'PROCEDURE' ORDER BY 1", cancellationToken),
            NodeKind.EventsFolder => NamesAsync(node, NodeKind.Event, "SELECT event_name AS name FROM information_schema.events WHERE event_schema = @s ORDER BY 1", cancellationToken),
            NodeKind.Table => RelationChildrenAsync(node, full: true, cancellationToken),
            NodeKind.View => RelationChildrenAsync(node, full: false, cancellationToken),
            NodeKind.ColumnsFolder => ColumnsAsync(node, cancellationToken),
            NodeKind.ConstraintsFolder => ConstraintsAsync(node, cancellationToken),
            NodeKind.IndexesFolder => IndexesAsync(node, cancellationToken),
            NodeKind.TriggersFolder => NamesAsync(node, NodeKind.Trigger, "SELECT trigger_name AS name FROM information_schema.triggers WHERE event_object_schema = @s AND event_object_table = @r ORDER BY 1", cancellationToken, ("@r", node.Relation)),
            _ => Task.FromResult<IReadOnlyList<TreeNode>>([]),
        };

    /// <inheritdoc />
    public override Task<ObjectProperties> GetPropertiesAsync(NodeRef node, CancellationToken cancellationToken = default) =>
        node.Kind switch
        {
            NodeKind.Server => ServerPropertiesAsync(cancellationToken),
            NodeKind.Database => ShowCreateAsync(node, $"SHOW CREATE DATABASE {Q(node.Name!)}", "Create Database", cancellationToken, DatabasePropsAsync),
            NodeKind.Table => ShowCreateAsync(node, $"SHOW CREATE TABLE {Q(node.Schema!)}.{Q(node.Name!)}", "Create Table", cancellationToken, TablePropsAsync),
            NodeKind.View => ShowCreateAsync(node, $"SHOW CREATE VIEW {Q(node.Schema!)}.{Q(node.Name!)}", "Create View", cancellationToken),
            NodeKind.Function => ShowCreateAsync(node, $"SHOW CREATE FUNCTION {Q(node.Schema!)}.{Q(node.Name!)}", "Create Function", cancellationToken),
            NodeKind.Procedure => ShowCreateAsync(node, $"SHOW CREATE PROCEDURE {Q(node.Schema!)}.{Q(node.Name!)}", "Create Procedure", cancellationToken),
            NodeKind.Event => ShowCreateAsync(node, $"SHOW CREATE EVENT {Q(node.Schema!)}.{Q(node.Name!)}", "Create Event", cancellationToken),
            NodeKind.Trigger => ShowCreateAsync(node, $"SHOW CREATE TRIGGER {Q(node.Schema!)}.{Q(node.Name!)}", "SQL Original Statement", cancellationToken),
            NodeKind.Column => ColumnPropertiesAsync(node, cancellationToken),
            NodeKind.LoginRole => UserPropertiesAsync(node, cancellationToken),
            _ => Task.FromResult(new ObjectProperties { Title = node.Name ?? node.Kind.ToString() }),
        };

    private static string Q(string name) => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";

    private async Task<IReadOnlyList<TreeNode>> ServerChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var databases = await TryScalarAsync($"SELECT count(*) FROM information_schema.schemata WHERE {UserDatabases}", ct).ConfigureAwait(false);
        var users = await TryScalarAsync("SELECT count(*) FROM mysql.user", ct).ConfigureAwait(false);
        return
        [
            Folder(node, NodeKind.DatabasesFolder, "Databases", ToLong(databases)),
            Folder(node, NodeKind.RolesFolder, "Users", ToLong(users)),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> DatabasesAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            $"SELECT schema_name AS name FROM information_schema.schemata WHERE {UserDatabases} ORDER BY 1",
            reader =>
            {
                var name = Str(reader, "name");
                return new TreeNode { Ref = folder.Item(NodeKind.Database, name) with { Database = name, Schema = name }, Label = name, HasChildren = true };
            },
            ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> UsersAsync(NodeRef folder, CancellationToken ct)
    {
        try
        {
            return await QueryAsync(
                "SELECT user, host FROM mysql.user ORDER BY user, host",
                reader => Item(folder, NodeKind.LoginRole, Str(reader, "user"), label: $"{Str(reader, "user")}@{Str(reader, "host")}", signature: Str(reader, "host")),
                ct).ConfigureAwait(false);
        }
        catch (IntrospectionException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<TreeNode>> DatabaseChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var counts = await RowAsync(
            """
            SELECT (SELECT count(*) FROM information_schema.tables WHERE table_schema = @s AND table_type = 'BASE TABLE') AS tables,
                   (SELECT count(*) FROM information_schema.tables WHERE table_schema = @s AND table_type = 'VIEW') AS views,
                   (SELECT count(*) FROM information_schema.routines WHERE routine_schema = @s AND routine_type = 'FUNCTION') AS functions,
                   (SELECT count(*) FROM information_schema.routines WHERE routine_schema = @s AND routine_type = 'PROCEDURE') AS procedures,
                   (SELECT count(*) FROM information_schema.events WHERE event_schema = @s) AS events
            """,
            ct,
            ("@s", node.Schema)).ConfigureAwait(false);

        return
        [
            Folder(node, NodeKind.TablesFolder, "Tables", Long(counts, "tables")),
            Folder(node, NodeKind.ViewsFolder, "Views", Long(counts, "views")),
            Folder(node, NodeKind.FunctionsFolder, "Functions", Long(counts, "functions")),
            Folder(node, NodeKind.ProceduresFolder, "Procedures", Long(counts, "procedures")),
            Folder(node, NodeKind.EventsFolder, "Events", Long(counts, "events")),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> RelationsAsync(NodeRef folder, NodeKind kind, string type, CancellationToken ct) =>
        await QueryAsync(
            "SELECT table_name AS name FROM information_schema.tables WHERE table_schema = @s AND table_type = @t ORDER BY 1",
            reader =>
            {
                var name = Str(reader, "name");
                return new TreeNode { Ref = folder.Item(kind, name) with { Relation = name }, Label = name, HasChildren = true };
            },
            ct,
            ("@s", folder.Schema),
            ("@t", type)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> RelationChildrenAsync(NodeRef node, bool full, CancellationToken ct)
    {
        var counts = await RowAsync(
            """
            SELECT (SELECT count(*) FROM information_schema.columns WHERE table_schema = @s AND table_name = @r) AS columns,
                   (SELECT count(*) FROM information_schema.table_constraints WHERE table_schema = @s AND table_name = @r) AS constraints,
                   (SELECT count(DISTINCT index_name) FROM information_schema.statistics s WHERE s.table_schema = @s AND s.table_name = @r
                      AND NOT EXISTS (SELECT 1 FROM information_schema.table_constraints k WHERE k.table_schema = s.table_schema
                                        AND k.table_name = s.table_name AND k.constraint_name = s.index_name)) AS indexes,
                   (SELECT count(*) FROM information_schema.triggers WHERE event_object_schema = @s AND event_object_table = @r) AS triggers
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
            "SELECT column_name AS name, column_type AS type, column_key AS ckey FROM information_schema.columns WHERE table_schema = @s AND table_name = @r ORDER BY ordinal_position",
            reader => Item(folder, NodeKind.Column, Str(reader, "name"), detail: Str(reader, "type"), badge: Str(reader, "ckey") == "PRI" ? "PK" : null),
            ct,
            ("@s", folder.Schema),
            ("@r", folder.Relation)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> ConstraintsAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            "SELECT constraint_name AS name, constraint_type AS type FROM information_schema.table_constraints WHERE table_schema = @s AND table_name = @r ORDER BY FIELD(constraint_type, 'PRIMARY KEY', 'FOREIGN KEY', 'UNIQUE', 'CHECK'), constraint_name",
            reader => Item(
                folder,
                Str(reader, "type") switch
                {
                    "PRIMARY KEY" => NodeKind.PrimaryKey,
                    "FOREIGN KEY" => NodeKind.ForeignKey,
                    "UNIQUE" => NodeKind.UniqueConstraint,
                    _ => NodeKind.CheckConstraint,
                },
                Str(reader, "name")),
            ct,
            ("@s", folder.Schema),
            ("@r", folder.Relation)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> IndexesAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            """
            SELECT index_name AS name, MIN(non_unique) AS non_unique, MIN(index_type) AS method
            FROM information_schema.statistics s
            WHERE s.table_schema = @s AND s.table_name = @r
              AND NOT EXISTS (SELECT 1 FROM information_schema.table_constraints k WHERE k.table_schema = s.table_schema
                                AND k.table_name = s.table_name AND k.constraint_name = s.index_name)
            GROUP BY index_name ORDER BY index_name
            """,
            reader => Item(folder, NodeKind.Index, Str(reader, "name"), detail: ((LongOrNull(reader, "non_unique") ?? 1) == 0 ? "unique " : string.Empty) + Str(reader, "method").ToLowerInvariant()),
            ct,
            ("@s", folder.Schema),
            ("@r", folder.Relation)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> NamesAsync(
        NodeRef folder,
        NodeKind kind,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] extra) =>
        await QueryAsync(
            sql,
            reader => Item(folder, kind, Str(reader, "name")),
            ct,
            [("@s", folder.Schema), .. extra]).ConfigureAwait(false);

    private async Task<ObjectProperties> ServerPropertiesAsync(CancellationToken ct)
    {
        var row = await RowAsync("SELECT VERSION() AS version, CURRENT_USER() AS user, @@hostname AS host, @@port AS port, @@character_set_server AS charset, @@collation_server AS collation", ct).ConfigureAwait(false);
        return new ObjectProperties
        {
            Title = "Server",
            Properties = Props(("Version", Get(row, "version")), ("Connected as", Get(row, "user")), ("Host", Get(row, "host")), ("Port", Get(row, "port")), ("Character set", Get(row, "charset")), ("Collation", Get(row, "collation"))),
        };
    }

    private async Task<ObjectProperties> ShowCreateAsync(
        NodeRef node,
        string sql,
        string column,
        CancellationToken ct,
        Func<NodeRef, CancellationToken, Task<IReadOnlyList<PropertyItem>>>? properties = null)
    {
        var row = await RowAsync(sql, ct).ConfigureAwait(false);
        var props = properties is null
            ? Props(("Name", node.Name), ("Database", node.Schema))
            : await properties(node, ct).ConfigureAwait(false);
        return new ObjectProperties
        {
            Title = node.Name ?? string.Empty,
            Properties = props,
            Sql = Get(row, column) is { } definition ? definition + ";" : null,
        };
    }

    private async Task<IReadOnlyList<PropertyItem>> DatabasePropsAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT default_character_set_name AS charset, default_collation_name AS collation,
                   (SELECT ROUND(SUM(data_length + index_length) / 1024 / 1024, 1) FROM information_schema.tables WHERE table_schema = @s) AS size_mb
            FROM information_schema.schemata WHERE schema_name = @s
            """,
            ct,
            ("@s", node.Name)).ConfigureAwait(false);
        return Props(("Name", node.Name), ("Character set", Get(row, "charset")), ("Collation", Get(row, "collation")), ("Size (MB)", Get(row, "size_mb")));
    }

    private async Task<IReadOnlyList<PropertyItem>> TablePropsAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT engine, table_rows, ROUND(data_length / 1024 / 1024, 2) AS data_mb, ROUND(index_length / 1024 / 1024, 2) AS index_mb,
                   auto_increment, create_time, update_time, table_collation, table_comment
            FROM information_schema.tables WHERE table_schema = @s AND table_name = @n
            """,
            ct,
            ("@s", node.Schema),
            ("@n", node.Name)).ConfigureAwait(false);
        return Props(
            ("Name", node.Name),
            ("Database", node.Schema),
            ("Engine", Get(row, "engine")),
            ("Rows (estimated)", Get(row, "table_rows")),
            ("Data size (MB)", Get(row, "data_mb")),
            ("Index size (MB)", Get(row, "index_mb")),
            ("Auto increment", Get(row, "auto_increment")),
            ("Collation", Get(row, "table_collation")),
            ("Created", Get(row, "create_time")),
            ("Updated", Get(row, "update_time")),
            ("Comment", string.IsNullOrEmpty(Get(row, "table_comment")) ? null : Get(row, "table_comment")));
    }

    private async Task<ObjectProperties> ColumnPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT ordinal_position, column_type, is_nullable, column_default, column_key, extra, character_set_name, collation_name, column_comment
            FROM information_schema.columns WHERE table_schema = @s AND table_name = @r AND column_name = @n
            """,
            ct,
            ("@s", node.Schema),
            ("@r", node.Relation),
            ("@n", node.Name)).ConfigureAwait(false);

        var definition = $"{Q(node.Name!)} {Get(row, "column_type")}" +
                         (Get(row, "is_nullable") == "NO" ? " NOT NULL" : " NULL") +
                         (Get(row, "column_default") is { } dflt ? $" DEFAULT {dflt}" : string.Empty) +
                         (string.IsNullOrEmpty(Get(row, "extra")) ? string.Empty : " " + Get(row, "extra")!.ToUpperInvariant());
        return new ObjectProperties
        {
            Title = $"{node.Relation}.{node.Name}",
            Properties = Props(
                ("Name", node.Name),
                ("Position", Get(row, "ordinal_position")),
                ("Type", Get(row, "column_type")),
                ("Nullable", Get(row, "is_nullable")),
                ("Default", Get(row, "column_default")),
                ("Key", Get(row, "column_key")),
                ("Extra", Get(row, "extra")),
                ("Character set", Get(row, "character_set_name")),
                ("Collation", Get(row, "collation_name")),
                ("Comment", string.IsNullOrEmpty(Get(row, "column_comment")) ? null : Get(row, "column_comment"))),
            Sql = $"ALTER TABLE {Q(node.Schema!)}.{Q(node.Relation!)}\n    MODIFY COLUMN {definition};",
        };
    }

    private async Task<ObjectProperties> UserPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var grants = new List<string>();
        try
        {
            grants = await QueryAsync(
                $"SHOW GRANTS FOR '{node.Name!.Replace("'", "''", StringComparison.Ordinal)}'@'{(node.Signature ?? "%").Replace("'", "''", StringComparison.Ordinal)}'",
                reader => reader.GetString(0),
                ct).ConfigureAwait(false);
        }
        catch (IntrospectionException)
        {
        }

        return new ObjectProperties
        {
            Title = $"{node.Name}@{node.Signature}",
            Properties = Props(("User", node.Name), ("Host", node.Signature)),
            Sql = grants.Count == 0 ? null : string.Join(";\n", grants) + ";",
        };
    }

    private static long? ToLong(object? value) =>
        value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
}
