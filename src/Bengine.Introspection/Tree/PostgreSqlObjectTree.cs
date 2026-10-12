namespace Bengine.Introspection.Tree;

using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Bengine.Enums;
using Bengine.Introspection.Model;
using Bengine.Introspection.Providers;

/// <summary>
/// The PostgreSQL object tree, following pgAdmin's hierarchy: a server holds
/// Databases, Login/Group Roles, and Tablespaces; a database holds Casts,
/// Catalogs, Event Triggers, Extensions, Foreign Data Wrappers, Languages,
/// Publications, Schemas, and Subscriptions; a schema holds Aggregates through
/// Views; a table holds Columns, Constraints, Indexes, and Triggers. Every
/// folder carries its child count. Object SQL is produced by PostgreSQL's own
/// <c>pg_get_*def</c> functions where they exist, and tables are rendered by
/// Bengine's generator from the introspected AST.
/// </summary>
public sealed partial class PostgreSqlObjectTree : ObjectTreeBase
{
    private const string UserSchemas = "nspname NOT LIKE 'pg\\_%' AND nspname <> 'information_schema'";
    private const string RelationOid = "to_regclass(format('%I.%I', @s::text, @r::text))";
    private const string ObjectOid = "to_regclass(format('%I.%I', @s::text, @n::text))";

    private const string TableFilter = "c.relkind IN ('r','p') AND NOT c.relispartition";
    private const string FunctionFilter = "p.prokind = 'f' AND p.prorettype <> 'pg_catalog.trigger'::regtype";
    private const string TriggerFunctionFilter = "p.prokind = 'f' AND p.prorettype = 'pg_catalog.trigger'::regtype";
    private const string TypeFilter =
        "t.typtype IN ('b','c','e','r') AND t.typcategory <> 'A' " +
        "AND (t.typrelid = 0 OR EXISTS (SELECT 1 FROM pg_class tc WHERE tc.oid = t.typrelid AND tc.relkind = 'c'))";

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "authorization", "binary",
        "both", "case", "cast", "check", "collate", "collation", "column", "concurrently", "constraint", "create",
        "cross", "current_catalog", "current_date", "current_role", "current_schema", "current_time",
        "current_timestamp", "current_user", "default", "deferrable", "desc", "distinct", "do", "else", "end",
        "except", "false", "fetch", "for", "foreign", "freeze", "from", "full", "grant", "group", "having", "ilike",
        "in", "initially", "inner", "intersect", "into", "is", "isnull", "join", "lateral", "leading", "left",
        "like", "limit", "localtime", "localtimestamp", "natural", "not", "notnull", "null", "offset", "on", "only",
        "or", "order", "outer", "overlaps", "placing", "primary", "references", "returning", "right", "select",
        "session_user", "similar", "some", "symmetric", "system_user", "table", "tablesample", "then", "to",
        "trailing", "true", "union", "unique", "user", "using", "variadic", "verbose", "when", "where", "window",
        "with",
    };

    /// <summary>Creates the tree over an open PostgreSQL connection.</summary>
    /// <param name="connection">An open connection to the database the nodes belong to.</param>
    public PostgreSqlObjectTree(DbConnection connection)
        : base(connection, DatabaseProvider.PostgreSql)
    {
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<TreeNode>> GetChildrenAsync(NodeRef node, CancellationToken cancellationToken = default) =>
        node.Kind switch
        {
            NodeKind.Server => ServerChildrenAsync(node, cancellationToken),
            NodeKind.DatabasesFolder => DatabasesAsync(node, cancellationToken),
            NodeKind.RolesFolder => RolesAsync(node, cancellationToken),
            NodeKind.TablespacesFolder => SimpleAsync(node, NodeKind.Tablespace, "SELECT spcname AS name, NULL AS detail FROM pg_tablespace ORDER BY 1", cancellationToken),
            NodeKind.Database => DatabaseChildrenAsync(node, cancellationToken),
            NodeKind.CastsFolder => CastsAsync(node, cancellationToken),
            NodeKind.CatalogsFolder => Task.FromResult(Catalogs(node)),
            NodeKind.EventTriggersFolder => SimpleAsync(node, NodeKind.EventTrigger, "SELECT evtname AS name, evtevent AS detail FROM pg_event_trigger ORDER BY 1", cancellationToken),
            NodeKind.ExtensionsFolder => SimpleAsync(node, NodeKind.Extension, "SELECT extname AS name, extversion AS detail FROM pg_extension ORDER BY 1", cancellationToken),
            NodeKind.ForeignDataWrappersFolder => ForeignDataWrappersAsync(node, cancellationToken),
            NodeKind.ForeignDataWrapper => ForeignDataWrapperChildrenAsync(node, cancellationToken),
            NodeKind.ForeignServersFolder => SimpleAsync(node, NodeKind.ForeignServer, "SELECT s.srvname AS name, s.srvtype AS detail FROM pg_foreign_server s JOIN pg_foreign_data_wrapper w ON w.oid = s.srvfdw WHERE w.fdwname = @r ORDER BY 1", cancellationToken, ("@r", node.Relation)),
            NodeKind.LanguagesFolder => SimpleAsync(node, NodeKind.Language, "SELECT lanname AS name, NULL AS detail FROM pg_language WHERE lanispl ORDER BY 1", cancellationToken),
            NodeKind.PublicationsFolder => SimpleAsync(node, NodeKind.Publication, "SELECT pubname AS name, NULL AS detail FROM pg_publication ORDER BY 1", cancellationToken),
            NodeKind.SchemasFolder => SchemasAsync(node, cancellationToken),
            NodeKind.SubscriptionsFolder => SubscriptionsAsync(node, cancellationToken),
            NodeKind.Schema or NodeKind.Catalog => SchemaChildrenAsync(node, cancellationToken),
            NodeKind.AggregatesFolder => RoutinesAsync(node, NodeKind.Aggregate, "p.prokind = 'a'", cancellationToken),
            NodeKind.CollationsFolder => SimpleAsync(node, NodeKind.Collation, "SELECT DISTINCT c.collname AS name, NULL AS detail FROM pg_collation c JOIN pg_namespace n ON n.oid = c.collnamespace WHERE n.nspname = @s ORDER BY 1", cancellationToken, ("@s", node.Schema)),
            NodeKind.DomainsFolder => SimpleAsync(node, NodeKind.Domain, "SELECT t.typname AS name, format_type(t.typbasetype, t.typtypmod) AS detail FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = @s AND t.typtype = 'd' ORDER BY 1", cancellationToken, ("@s", node.Schema)),
            NodeKind.ForeignTablesFolder => RelationsAsync(node, NodeKind.ForeignTable, "c.relkind = 'f'", cancellationToken),
            NodeKind.FunctionsFolder => RoutinesAsync(node, NodeKind.Function, FunctionFilter, cancellationToken),
            NodeKind.MaterializedViewsFolder => RelationsAsync(node, NodeKind.MaterializedView, "c.relkind = 'm'", cancellationToken),
            NodeKind.ProceduresFolder => RoutinesAsync(node, NodeKind.Procedure, "p.prokind = 'p'", cancellationToken),
            NodeKind.SequencesFolder => RelationsAsync(node, NodeKind.Sequence, "c.relkind = 'S'", cancellationToken),
            NodeKind.TablesFolder => RelationsAsync(node, NodeKind.Table, TableFilter, cancellationToken),
            NodeKind.TriggerFunctionsFolder => RoutinesAsync(node, NodeKind.TriggerFunction, TriggerFunctionFilter, cancellationToken),
            NodeKind.TypesFolder => SimpleAsync(node, NodeKind.Type, $"SELECT t.typname AS name, CASE t.typtype WHEN 'e' THEN 'enum' WHEN 'c' THEN 'composite' WHEN 'r' THEN 'range' ELSE 'base' END AS detail FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = @s AND {TypeFilter} ORDER BY 1", cancellationToken, ("@s", node.Schema)),
            NodeKind.ViewsFolder => RelationsAsync(node, NodeKind.View, "c.relkind = 'v'", cancellationToken),
            NodeKind.Table => RelationChildrenAsync(node, constraints: true, indexes: true, triggers: true, cancellationToken),
            NodeKind.View => RelationChildrenAsync(node, constraints: false, indexes: false, triggers: true, cancellationToken),
            NodeKind.MaterializedView => RelationChildrenAsync(node, constraints: false, indexes: true, triggers: false, cancellationToken),
            NodeKind.ForeignTable => RelationChildrenAsync(node, constraints: true, indexes: false, triggers: true, cancellationToken),
            NodeKind.ColumnsFolder => ColumnsAsync(node, cancellationToken),
            NodeKind.ConstraintsFolder => ConstraintsAsync(node, cancellationToken),
            NodeKind.IndexesFolder => IndexesAsync(node, cancellationToken),
            NodeKind.TriggersFolder => TriggersAsync(node, cancellationToken),
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
            NodeKind.ForeignTable => ForeignTablePropertiesAsync(node, cancellationToken),
            NodeKind.View or NodeKind.MaterializedView => ViewPropertiesAsync(node, cancellationToken),
            NodeKind.Sequence => SequencePropertiesAsync(node, cancellationToken),
            NodeKind.Function or NodeKind.Procedure or NodeKind.TriggerFunction or NodeKind.Aggregate => RoutinePropertiesAsync(node, cancellationToken),
            NodeKind.Column => ColumnPropertiesAsync(node, cancellationToken),
            NodeKind.PrimaryKey or NodeKind.ForeignKey or NodeKind.UniqueConstraint or NodeKind.CheckConstraint or NodeKind.ExclusionConstraint => ConstraintPropertiesAsync(node, cancellationToken),
            NodeKind.Index => IndexPropertiesAsync(node, cancellationToken),
            NodeKind.Trigger => TriggerPropertiesAsync(node, cancellationToken),
            NodeKind.LoginRole or NodeKind.GroupRole => RolePropertiesAsync(node, cancellationToken),
            NodeKind.Tablespace => TablespacePropertiesAsync(node, cancellationToken),
            NodeKind.Extension => ExtensionPropertiesAsync(node, cancellationToken),
            NodeKind.Language => LanguagePropertiesAsync(node, cancellationToken),
            NodeKind.Cast => CastPropertiesAsync(node, cancellationToken),
            NodeKind.EventTrigger => EventTriggerPropertiesAsync(node, cancellationToken),
            NodeKind.ForeignDataWrapper => ForeignDataWrapperPropertiesAsync(node, cancellationToken),
            NodeKind.ForeignServer => ForeignServerPropertiesAsync(node, cancellationToken),
            NodeKind.Publication => PublicationPropertiesAsync(node, cancellationToken),
            NodeKind.Subscription => SubscriptionPropertiesAsync(node, cancellationToken),
            NodeKind.Type => TypePropertiesAsync(node, cancellationToken),
            NodeKind.Domain => DomainPropertiesAsync(node, cancellationToken),
            NodeKind.Collation => CollationPropertiesAsync(node, cancellationToken),
            _ => Task.FromResult(new ObjectProperties { Title = node.Name ?? node.Kind.ToString() }),
        };

    // ------------------------------------------------------------------ children

    private async Task<IReadOnlyList<TreeNode>> ServerChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var counts = await RowAsync(
            $"""
            SELECT (SELECT count(*) FROM pg_database WHERE NOT datistemplate) AS databases,
                   (SELECT count(*) FROM pg_roles WHERE rolname NOT LIKE 'pg\_%') AS roles,
                   (SELECT count(*) FROM pg_tablespace) AS tablespaces
            """,
            ct).ConfigureAwait(false);

        return
        [
            Folder(node, NodeKind.DatabasesFolder, "Databases", Long(counts, "databases")),
            Folder(node, NodeKind.RolesFolder, "Login/Group Roles", Long(counts, "roles")),
            Folder(node, NodeKind.TablespacesFolder, "Tablespaces", Long(counts, "tablespaces")),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> DatabasesAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            "SELECT datname, datallowconn FROM pg_database WHERE NOT datistemplate ORDER BY datname",
            reader =>
            {
                var name = Str(reader, "datname");
                return new TreeNode
                {
                    Ref = folder.Item(NodeKind.Database, name) with { Database = name },
                    Label = name,
                    HasChildren = Bool(reader, "datallowconn"),
                };
            },
            ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> RolesAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            "SELECT rolname, rolcanlogin FROM pg_roles WHERE rolname NOT LIKE 'pg\\_%' ORDER BY rolname",
            reader => Item(folder, Bool(reader, "rolcanlogin") ? NodeKind.LoginRole : NodeKind.GroupRole, Str(reader, "rolname")),
            ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> DatabaseChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var counts = await RowAsync(
            $"""
            SELECT (SELECT count(*) FROM pg_cast WHERE oid >= 16384) AS casts,
                   (SELECT count(*) FROM pg_event_trigger) AS event_triggers,
                   (SELECT count(*) FROM pg_extension) AS extensions,
                   (SELECT count(*) FROM pg_foreign_data_wrapper) AS fdws,
                   (SELECT count(*) FROM pg_language WHERE lanispl) AS languages,
                   (SELECT count(*) FROM pg_publication) AS publications,
                   (SELECT count(*) FROM pg_namespace WHERE {UserSchemas}) AS schemas
            """,
            ct).ConfigureAwait(false);

        var subscriptions = await TryScalarAsync(
            "SELECT count(*) FROM pg_subscription WHERE subdbid = (SELECT oid FROM pg_database WHERE datname = current_database())",
            ct).ConfigureAwait(false);

        var scope = node with { Database = node.Name };
        return
        [
            Folder(scope, NodeKind.CastsFolder, "Casts", Long(counts, "casts")),
            Folder(scope, NodeKind.CatalogsFolder, "Catalogs", 2),
            Folder(scope, NodeKind.EventTriggersFolder, "Event Triggers", Long(counts, "event_triggers")),
            Folder(scope, NodeKind.ExtensionsFolder, "Extensions", Long(counts, "extensions")),
            Folder(scope, NodeKind.ForeignDataWrappersFolder, "Foreign Data Wrappers", Long(counts, "fdws")),
            Folder(scope, NodeKind.LanguagesFolder, "Languages", Long(counts, "languages")),
            Folder(scope, NodeKind.PublicationsFolder, "Publications", Long(counts, "publications")),
            Folder(scope, NodeKind.SchemasFolder, "Schemas", Long(counts, "schemas")),
            Folder(scope, NodeKind.SubscriptionsFolder, "Subscriptions", subscriptions is null ? null : Convert.ToInt64(subscriptions, System.Globalization.CultureInfo.InvariantCulture)),
        ];
    }

    private static IReadOnlyList<TreeNode> Catalogs(NodeRef folder) =>
    [
        new TreeNode
        {
            Ref = folder.Item(NodeKind.Catalog, "pg_catalog") with { Schema = "pg_catalog" },
            Label = "PostgreSQL Catalog (pg_catalog)",
            HasChildren = true,
        },
        new TreeNode
        {
            Ref = folder.Item(NodeKind.Catalog, "information_schema") with { Schema = "information_schema" },
            Label = "ANSI (information_schema)",
            HasChildren = true,
        },
    ];

    private async Task<IReadOnlyList<TreeNode>> SchemasAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            $"SELECT nspname FROM pg_namespace WHERE {UserSchemas} ORDER BY nspname",
            reader =>
            {
                var name = Str(reader, "nspname");
                return new TreeNode
                {
                    Ref = folder.Item(NodeKind.Schema, name) with { Schema = name },
                    Label = name,
                    HasChildren = true,
                };
            },
            ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> SubscriptionsAsync(NodeRef folder, CancellationToken ct)
    {
        try
        {
            return await SimpleAsync(
                folder,
                NodeKind.Subscription,
                "SELECT subname AS name, NULL AS detail FROM pg_subscription WHERE subdbid = (SELECT oid FROM pg_database WHERE datname = current_database()) ORDER BY 1",
                ct).ConfigureAwait(false);
        }
        catch (IntrospectionException)
        {
            // pg_subscription can be unreadable without elevated privileges.
            return [];
        }
    }

    private async Task<IReadOnlyList<TreeNode>> CastsAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            "SELECT c.oid::text AS oid, format_type(c.castsource, NULL) || ' -> ' || format_type(c.casttarget, NULL) AS name FROM pg_cast c WHERE c.oid >= 16384 ORDER BY 2",
            reader => Item(folder, NodeKind.Cast, Str(reader, "name"), signature: Str(reader, "oid")),
            ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> ForeignDataWrappersAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            "SELECT fdwname FROM pg_foreign_data_wrapper ORDER BY 1",
            reader =>
            {
                var name = Str(reader, "fdwname");
                return new TreeNode
                {
                    Ref = folder.Item(NodeKind.ForeignDataWrapper, name) with { Relation = name },
                    Label = name,
                    HasChildren = true,
                };
            },
            ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> ForeignDataWrapperChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var count = await TryScalarAsync(
            "SELECT count(*) FROM pg_foreign_server s JOIN pg_foreign_data_wrapper w ON w.oid = s.srvfdw WHERE w.fdwname = @r",
            ct,
            ("@r", node.Relation)).ConfigureAwait(false);
        return [Folder(node, NodeKind.ForeignServersFolder, "Foreign Servers", count is null ? null : Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture))];
    }

    private async Task<IReadOnlyList<TreeNode>> SchemaChildrenAsync(NodeRef node, CancellationToken ct)
    {
        var counts = await RowAsync(
            $"""
            WITH ns AS (SELECT oid FROM pg_namespace WHERE nspname = @s)
            SELECT
              (SELECT count(*) FROM pg_proc p, ns WHERE p.pronamespace = ns.oid AND p.prokind = 'a') AS aggregates,
              (SELECT count(DISTINCT c.collname) FROM pg_collation c, ns WHERE c.collnamespace = ns.oid) AS collations,
              (SELECT count(*) FROM pg_type t, ns WHERE t.typnamespace = ns.oid AND t.typtype = 'd') AS domains,
              (SELECT count(*) FROM pg_class c, ns WHERE c.relnamespace = ns.oid AND c.relkind = 'f') AS foreign_tables,
              (SELECT count(*) FROM pg_proc p, ns WHERE p.pronamespace = ns.oid AND {FunctionFilter}) AS functions,
              (SELECT count(*) FROM pg_class c, ns WHERE c.relnamespace = ns.oid AND c.relkind = 'm') AS matviews,
              (SELECT count(*) FROM pg_proc p, ns WHERE p.pronamespace = ns.oid AND p.prokind = 'p') AS procedures,
              (SELECT count(*) FROM pg_class c, ns WHERE c.relnamespace = ns.oid AND c.relkind = 'S') AS sequences,
              (SELECT count(*) FROM pg_class c, ns WHERE c.relnamespace = ns.oid AND {TableFilter}) AS tables,
              (SELECT count(*) FROM pg_proc p, ns WHERE p.pronamespace = ns.oid AND {TriggerFunctionFilter}) AS trigger_functions,
              (SELECT count(*) FROM pg_type t, ns WHERE t.typnamespace = ns.oid AND {TypeFilter}) AS types,
              (SELECT count(*) FROM pg_class c, ns WHERE c.relnamespace = ns.oid AND c.relkind = 'v') AS views
            """,
            ct,
            ("@s", node.Schema)).ConfigureAwait(false);

        return
        [
            Folder(node, NodeKind.AggregatesFolder, "Aggregates", Long(counts, "aggregates")),
            Folder(node, NodeKind.CollationsFolder, "Collations", Long(counts, "collations")),
            Folder(node, NodeKind.DomainsFolder, "Domains", Long(counts, "domains")),
            Folder(node, NodeKind.ForeignTablesFolder, "Foreign Tables", Long(counts, "foreign_tables")),
            Folder(node, NodeKind.FunctionsFolder, "Functions", Long(counts, "functions")),
            Folder(node, NodeKind.MaterializedViewsFolder, "Materialized Views", Long(counts, "matviews")),
            Folder(node, NodeKind.ProceduresFolder, "Procedures", Long(counts, "procedures")),
            Folder(node, NodeKind.SequencesFolder, "Sequences", Long(counts, "sequences")),
            Folder(node, NodeKind.TablesFolder, "Tables", Long(counts, "tables")),
            Folder(node, NodeKind.TriggerFunctionsFolder, "Trigger Functions", Long(counts, "trigger_functions")),
            Folder(node, NodeKind.TypesFolder, "Types", Long(counts, "types")),
            Folder(node, NodeKind.ViewsFolder, "Views", Long(counts, "views")),
        ];
    }

    private async Task<IReadOnlyList<TreeNode>> RelationsAsync(NodeRef folder, NodeKind kind, string filter, CancellationToken ct)
    {
        var expandable = kind is not NodeKind.Sequence;
        return await QueryAsync(
            $"SELECT c.relname, c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @s AND {filter} ORDER BY c.relname",
            reader =>
            {
                var name = Str(reader, "relname");
                return new TreeNode
                {
                    Ref = folder.Item(kind, name) with { Relation = name },
                    Label = name,
                    Detail = Str(reader, "relkind") == "p" ? "partitioned" : null,
                    HasChildren = expandable,
                };
            },
            ct,
            ("@s", folder.Schema)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TreeNode>> RoutinesAsync(NodeRef folder, NodeKind kind, string filter, CancellationToken ct) =>
        await QueryAsync(
            $"SELECT p.proname, pg_get_function_identity_arguments(p.oid) AS args FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = @s AND {filter} ORDER BY p.proname, 2",
            reader =>
            {
                var name = Str(reader, "proname");
                var args = Str(reader, "args");
                return Item(folder, kind, name, label: $"{name}({args})", signature: args);
            },
            ct,
            ("@s", folder.Schema)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> RelationChildrenAsync(
        NodeRef node,
        bool constraints,
        bool indexes,
        bool triggers,
        CancellationToken ct)
    {
        var counts = await RowAsync(
            $"""
            SELECT
              (SELECT count(*) FROM pg_attribute WHERE attrelid = r.oid AND attnum > 0 AND NOT attisdropped) AS columns,
              (SELECT count(*) FROM pg_constraint WHERE conrelid = r.oid AND contype IN ('p','f','u','c','x')) AS constraints,
              (SELECT count(*) FROM pg_index ix WHERE ix.indrelid = r.oid
                 AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = ix.indexrelid AND k.contype IN ('p','u','x'))) AS indexes,
              (SELECT count(*) FROM pg_trigger WHERE tgrelid = r.oid AND NOT tgisinternal) AS triggers
            FROM (SELECT {RelationOid}::oid AS oid) r
            """,
            ct,
            ("@s", node.Schema),
            ("@r", node.Relation)).ConfigureAwait(false);

        var folders = new List<TreeNode> { Folder(node, NodeKind.ColumnsFolder, "Columns", Long(counts, "columns")) };
        if (constraints)
        {
            folders.Add(Folder(node, NodeKind.ConstraintsFolder, "Constraints", Long(counts, "constraints")));
        }

        if (indexes)
        {
            folders.Add(Folder(node, NodeKind.IndexesFolder, "Indexes", Long(counts, "indexes")));
        }

        if (triggers)
        {
            folders.Add(Folder(node, NodeKind.TriggersFolder, "Triggers", Long(counts, "triggers")));
        }

        return folders;
    }

    private async Task<IReadOnlyList<TreeNode>> ColumnsAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            $"""
            SELECT a.attname, format_type(a.atttypid, a.atttypmod) AS type,
                   EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conrelid = a.attrelid AND k.contype = 'p' AND a.attnum = ANY (k.conkey)) AS pk,
                   EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conrelid = a.attrelid AND k.contype = 'f' AND a.attnum = ANY (k.conkey)) AS fk
            FROM pg_attribute a
            WHERE a.attrelid = {RelationOid} AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """,
            reader => Item(
                folder,
                NodeKind.Column,
                Str(reader, "attname"),
                detail: Str(reader, "type"),
                badge: Bool(reader, "pk") ? "PK" : Bool(reader, "fk") ? "FK" : null),
            ct,
            ("@s", folder.Schema),
            ("@r", folder.Relation)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> ConstraintsAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            $"""
            SELECT conname, contype FROM pg_constraint
            WHERE conrelid = {RelationOid} AND contype IN ('p','f','u','c','x')
            ORDER BY CASE contype WHEN 'p' THEN 0 WHEN 'f' THEN 1 WHEN 'u' THEN 2 WHEN 'c' THEN 3 ELSE 4 END, conname
            """,
            reader =>
            {
                var kind = Str(reader, "contype") switch
                {
                    "p" => NodeKind.PrimaryKey,
                    "f" => NodeKind.ForeignKey,
                    "u" => NodeKind.UniqueConstraint,
                    "c" => NodeKind.CheckConstraint,
                    _ => NodeKind.ExclusionConstraint,
                };
                return Item(folder, kind, Str(reader, "conname"));
            },
            ct,
            ("@s", folder.Schema),
            ("@r", folder.Relation)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> IndexesAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            $"""
            SELECT ic.relname, ix.indisunique, am.amname
            FROM pg_index ix
            JOIN pg_class ic ON ic.oid = ix.indexrelid
            JOIN pg_am am ON am.oid = ic.relam
            WHERE ix.indrelid = {RelationOid}
              AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = ix.indexrelid AND k.contype IN ('p','u','x'))
            ORDER BY ic.relname
            """,
            reader => Item(
                folder,
                NodeKind.Index,
                Str(reader, "relname"),
                detail: (Bool(reader, "indisunique") ? "unique " : string.Empty) + Str(reader, "amname")),
            ct,
            ("@s", folder.Schema),
            ("@r", folder.Relation)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> TriggersAsync(NodeRef folder, CancellationToken ct) =>
        await QueryAsync(
            $"SELECT tgname, tgenabled FROM pg_trigger WHERE tgrelid = {RelationOid} AND NOT tgisinternal ORDER BY tgname",
            reader => Item(
                folder,
                NodeKind.Trigger,
                Str(reader, "tgname"),
                detail: Str(reader, "tgenabled") == "D" ? "disabled" : null),
            ct,
            ("@s", folder.Schema),
            ("@r", folder.Relation)).ConfigureAwait(false);

    private async Task<IReadOnlyList<TreeNode>> SimpleAsync(
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

    // ---------------------------------------------------------------- properties

    private async Task<ObjectProperties> ServerPropertiesAsync(CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT version() AS version, current_user AS "user", current_database() AS maintenance_db,
                   inet_server_addr()::text AS address, inet_server_port() AS port,
                   pg_postmaster_start_time() AS started, current_setting('server_encoding') AS encoding,
                   current_setting('TimeZone') AS timezone, current_setting('max_connections') AS max_connections,
                   (SELECT count(*) FROM pg_stat_activity) AS sessions
            """,
            ct).ConfigureAwait(false);

        return new ObjectProperties
        {
            Title = "Server",
            Properties = Props(
                ("Version", Get(row, "version")),
                ("Connected as", Get(row, "user")),
                ("Maintenance database", Get(row, "maintenance_db")),
                ("Address", Get(row, "address")),
                ("Port", Get(row, "port")),
                ("Started", Get(row, "started")),
                ("Server encoding", Get(row, "encoding")),
                ("Time zone", Get(row, "timezone")),
                ("Max connections", Get(row, "max_connections")),
                ("Active sessions", Get(row, "sessions"))),
        };
    }

    private async Task<ObjectProperties> DatabasePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT d.datname, pg_get_userbyid(d.datdba) AS owner, pg_encoding_to_char(d.encoding) AS encoding,
                   d.datcollate AS collate, d.datctype AS ctype, d.datconnlimit AS connlimit, t.spcname AS tablespace,
                   d.datallowconn AS allowconn, d.datistemplate AS istemplate,
                   shobj_description(d.oid, 'pg_database') AS comment,
                   CASE WHEN has_database_privilege(d.oid, 'CONNECT') THEN pg_size_pretty(pg_database_size(d.oid)) END AS size
            FROM pg_database d JOIN pg_tablespace t ON t.oid = d.dattablespace
            WHERE d.datname = @n
            """,
            ct,
            ("@n", node.Name)).ConfigureAwait(false);

        var name = node.Name!;
        var sql = new StringBuilder()
            .AppendLine($"-- Database: {name}")
            .AppendLine()
            .AppendLine($"-- DROP DATABASE IF EXISTS {Q(name)};")
            .AppendLine()
            .AppendLine($"CREATE DATABASE {Q(name)}")
            .AppendLine("    WITH")
            .AppendLine($"    OWNER = {Q(Get(row, "owner") ?? "postgres")}")
            .AppendLine($"    ENCODING = {Lit(Get(row, "encoding"))}")
            .AppendLine($"    LC_COLLATE = {Lit(Get(row, "collate"))}")
            .AppendLine($"    LC_CTYPE = {Lit(Get(row, "ctype"))}")
            .AppendLine($"    TABLESPACE = {Q(Get(row, "tablespace") ?? "pg_default")}")
            .AppendLine($"    CONNECTION LIMIT = {Get(row, "connlimit")}")
            .Append($"    IS_TEMPLATE = {(Get(row, "istemplate") == "Yes" ? "True" : "False")};")
            .Append(CommentOn("DATABASE", Q(name), Get(row, "comment")));

        return new ObjectProperties
        {
            Title = name,
            Properties = Props(
                ("Name", name),
                ("Owner", Get(row, "owner")),
                ("Size", Get(row, "size")),
                ("Encoding", Get(row, "encoding")),
                ("Collation", Get(row, "collate")),
                ("Character type", Get(row, "ctype")),
                ("Tablespace", Get(row, "tablespace")),
                ("Connection limit", Get(row, "connlimit")),
                ("Allow connections", Get(row, "allowconn")),
                ("Is template", Get(row, "istemplate")),
                ("Comment", Get(row, "comment"))),
            Sql = sql.ToString(),
        };
    }

    private async Task<ObjectProperties> SchemaPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            "SELECT pg_get_userbyid(n.nspowner) AS owner, obj_description(n.oid, 'pg_namespace') AS comment, array_to_string(n.nspacl, ', ') AS acl FROM pg_namespace n WHERE n.nspname = @n",
            ct,
            ("@n", node.Name)).ConfigureAwait(false);

        var name = node.Name!;
        var sql = new StringBuilder()
            .AppendLine($"-- SCHEMA: {name}")
            .AppendLine()
            .AppendLine($"-- DROP SCHEMA IF EXISTS {Q(name)} ;")
            .AppendLine()
            .AppendLine($"CREATE SCHEMA IF NOT EXISTS {Q(name)}")
            .Append($"    AUTHORIZATION {Q(Get(row, "owner") ?? "postgres")};")
            .Append(CommentOn("SCHEMA", Q(name), Get(row, "comment")));

        return new ObjectProperties
        {
            Title = name,
            Properties = Props(("Name", name), ("Owner", Get(row, "owner")), ("Privileges", Get(row, "acl")), ("Comment", Get(row, "comment"))),
            Sql = sql.ToString(),
        };
    }

    private async Task<ObjectProperties> TablePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            $"""
            SELECT pg_get_userbyid(c.relowner) AS owner, COALESCE(t.spcname, 'pg_default') AS tablespace,
                   c.reltuples::bigint AS rows_estimate, c.relkind = 'p' AS partitioned, c.relrowsecurity AS rls,
                   pg_size_pretty(pg_total_relation_size(c.oid)) AS total_size,
                   pg_size_pretty(pg_relation_size(c.oid)) AS table_size,
                   pg_size_pretty(pg_indexes_size(c.oid)) AS index_size,
                   obj_description(c.oid, 'pg_class') AS comment,
                   s.n_live_tup AS live_rows, s.n_dead_tup AS dead_rows,
                   s.last_vacuum, s.last_autovacuum, s.last_analyze, s.last_autoanalyze
            FROM pg_class c
            LEFT JOIN pg_tablespace t ON t.oid = c.reltablespace
            LEFT JOIN pg_stat_all_tables s ON s.relid = c.oid
            WHERE c.oid = {ObjectOid}
            """,
            ct,
            ("@s", node.Schema),
            ("@n", node.Name)).ConfigureAwait(false);

        var qualified = $"{Q(node.Schema!)}.{Q(node.Name!)}";
        string ddl;
        try
        {
            var table = await new PostgreSqlSchemaProvider(Connection)
                .IntrospectTableAsync(new TableRef(node.Name!, node.Schema), ct)
                .ConfigureAwait(false);
            ddl = SchemaDdlRenderer.RenderTable(DatabaseProvider.PostgreSql, table).TrimEnd();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ddl = $"-- The table DDL could not be generated: {ex.Message}";
        }

        var sql = new StringBuilder()
            .AppendLine($"-- Table: {node.Schema}.{node.Name}")
            .AppendLine()
            .AppendLine($"-- DROP TABLE IF EXISTS {qualified};")
            .AppendLine()
            .AppendLine(ddl)
            .AppendLine()
            .AppendLine($"ALTER TABLE IF EXISTS {qualified}")
            .Append($"    OWNER to {Q(Get(row, "owner") ?? "postgres")};")
            .Append(CommentOn("TABLE", qualified, Get(row, "comment")));

        return new ObjectProperties
        {
            Title = $"{node.Schema}.{node.Name}",
            Properties = Props(
                ("Name", node.Name),
                ("Schema", node.Schema),
                ("Owner", Get(row, "owner")),
                ("Tablespace", Get(row, "tablespace")),
                ("Partitioned", Get(row, "partitioned")),
                ("Row level security", Get(row, "rls")),
                ("Rows (estimated)", Get(row, "rows_estimate")),
                ("Live rows", Get(row, "live_rows")),
                ("Dead rows", Get(row, "dead_rows")),
                ("Total size", Get(row, "total_size")),
                ("Table size", Get(row, "table_size")),
                ("Indexes size", Get(row, "index_size")),
                ("Last vacuum", Get(row, "last_vacuum") ?? Get(row, "last_autovacuum")),
                ("Last analyze", Get(row, "last_analyze") ?? Get(row, "last_autoanalyze")),
                ("Comment", Get(row, "comment"))),
            Sql = sql.ToString(),
        };
    }

    private async Task<ObjectProperties> ForeignTablePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            $"""
            SELECT pg_get_userbyid(c.relowner) AS owner, s.srvname AS server, array_to_string(ft.ftoptions, ', ') AS options,
                   obj_description(c.oid, 'pg_class') AS comment
            FROM pg_class c JOIN pg_foreign_table ft ON ft.ftrelid = c.oid JOIN pg_foreign_server s ON s.oid = ft.ftserver
            WHERE c.oid = {ObjectOid}
            """,
            ct,
            ("@s", node.Schema),
            ("@n", node.Name)).ConfigureAwait(false);

        return new ObjectProperties
        {
            Title = $"{node.Schema}.{node.Name}",
            Properties = Props(("Name", node.Name), ("Schema", node.Schema), ("Owner", Get(row, "owner")), ("Server", Get(row, "server")), ("Options", Get(row, "options")), ("Comment", Get(row, "comment"))),
        };
    }

    private async Task<ObjectProperties> ViewPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            $"""
            SELECT pg_get_userbyid(c.relowner) AS owner, obj_description(c.oid, 'pg_class') AS comment,
                   pg_get_viewdef(c.oid, true) AS definition, c.relispopulated AS populated,
                   pg_size_pretty(pg_total_relation_size(c.oid)) AS size, COALESCE(t.spcname, 'pg_default') AS tablespace
            FROM pg_class c LEFT JOIN pg_tablespace t ON t.oid = c.reltablespace
            WHERE c.oid = {ObjectOid}
            """,
            ct,
            ("@s", node.Schema),
            ("@n", node.Name)).ConfigureAwait(false);

        var qualified = $"{Q(node.Schema!)}.{Q(node.Name!)}";
        var definition = (Get(row, "definition") ?? string.Empty).TrimEnd().TrimEnd(';');
        var materialized = node.Kind == NodeKind.MaterializedView;
        var sql = new StringBuilder();
        if (materialized)
        {
            sql.AppendLine($"-- View: {node.Schema}.{node.Name}")
                .AppendLine()
                .AppendLine($"-- DROP MATERIALIZED VIEW IF EXISTS {qualified};")
                .AppendLine()
                .AppendLine($"CREATE MATERIALIZED VIEW IF NOT EXISTS {qualified}")
                .AppendLine($"TABLESPACE {Q(Get(row, "tablespace") ?? "pg_default")}")
                .AppendLine("AS")
                .AppendLine(definition)
                .AppendLine("WITH DATA;")
                .AppendLine()
                .AppendLine($"ALTER TABLE IF EXISTS {qualified}")
                .Append($"    OWNER TO {Q(Get(row, "owner") ?? "postgres")};")
                .Append(CommentOn("MATERIALIZED VIEW", qualified, Get(row, "comment")));
        }
        else
        {
            sql.AppendLine($"-- View: {node.Schema}.{node.Name}")
                .AppendLine()
                .AppendLine($"-- DROP VIEW {qualified};")
                .AppendLine()
                .AppendLine($"CREATE OR REPLACE VIEW {qualified}")
                .AppendLine(" AS")
                .AppendLine(definition + ";")
                .AppendLine()
                .AppendLine($"ALTER TABLE {qualified}")
                .Append($"    OWNER TO {Q(Get(row, "owner") ?? "postgres")};")
                .Append(CommentOn("VIEW", qualified, Get(row, "comment")));
        }

        return new ObjectProperties
        {
            Title = $"{node.Schema}.{node.Name}",
            Properties = Props(
                ("Name", node.Name),
                ("Schema", node.Schema),
                ("Owner", Get(row, "owner")),
                ("Size", materialized ? Get(row, "size") : null),
                ("Populated", materialized ? Get(row, "populated") : null),
                ("Comment", Get(row, "comment"))),
            Sql = sql.ToString(),
        };
    }

    private async Task<ObjectProperties> SequencePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            $"""
            SELECT s.data_type::text AS data_type, s.start_value, s.min_value, s.max_value, s.increment_by,
                   s.cycle, s.cache_size, s.last_value, pg_get_userbyid(c.relowner) AS owner,
                   obj_description(c.oid, 'pg_class') AS comment
            FROM pg_sequences s JOIN pg_class c ON c.oid = {ObjectOid}
            WHERE s.schemaname = @s AND s.sequencename = @n
            """,
            ct,
            ("@s", node.Schema),
            ("@n", node.Name)).ConfigureAwait(false);

        var qualified = $"{Q(node.Schema!)}.{Q(node.Name!)}";
        var sql = new StringBuilder()
            .AppendLine($"-- SEQUENCE: {node.Schema}.{node.Name}")
            .AppendLine()
            .AppendLine($"-- DROP SEQUENCE IF EXISTS {qualified};")
            .AppendLine()
            .AppendLine($"CREATE SEQUENCE IF NOT EXISTS {qualified}")
            .AppendLine($"    INCREMENT {Get(row, "increment_by")}")
            .AppendLine($"    START {Get(row, "start_value")}")
            .AppendLine($"    MINVALUE {Get(row, "min_value")}")
            .AppendLine($"    MAXVALUE {Get(row, "max_value")}")
            .Append($"    CACHE {Get(row, "cache_size")}")
            .AppendLine(Get(row, "cycle") == "Yes" ? Environment.NewLine + "    CYCLE;" : ";")
            .AppendLine()
            .AppendLine($"ALTER SEQUENCE {qualified}")
            .Append($"    OWNER TO {Q(Get(row, "owner") ?? "postgres")};")
            .Append(CommentOn("SEQUENCE", qualified, Get(row, "comment")));

        return new ObjectProperties
        {
            Title = $"{node.Schema}.{node.Name}",
            Properties = Props(
                ("Name", node.Name),
                ("Owner", Get(row, "owner")),
                ("Data type", Get(row, "data_type")),
                ("Current value", Get(row, "last_value")),
                ("Increment", Get(row, "increment_by")),
                ("Start", Get(row, "start_value")),
                ("Minimum", Get(row, "min_value")),
                ("Maximum", Get(row, "max_value")),
                ("Cache", Get(row, "cache_size")),
                ("Cycled", Get(row, "cycle")),
                ("Comment", Get(row, "comment"))),
            Sql = sql.ToString(),
        };
    }

    private async Task<ObjectProperties> RoutinePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT pg_get_userbyid(p.proowner) AS owner, l.lanname AS language, pg_get_function_result(p.oid) AS returns,
                   pg_get_function_arguments(p.oid) AS arguments,
                   CASE p.provolatile WHEN 'i' THEN 'IMMUTABLE' WHEN 's' THEN 'STABLE' ELSE 'VOLATILE' END AS volatility,
                   CASE p.proparallel WHEN 's' THEN 'SAFE' WHEN 'r' THEN 'RESTRICTED' ELSE 'UNSAFE' END AS parallel,
                   p.prosecdef AS security_definer, p.proisstrict AS strict, p.proretset AS returns_set,
                   p.procost AS cost, p.prorows AS rows, obj_description(p.oid, 'pg_proc') AS comment,
                   CASE WHEN p.prokind <> 'a' THEN pg_get_functiondef(p.oid) END AS definition,
                   ag.aggtransfn::text AS sfunc, format_type(ag.aggtranstype, NULL) AS stype,
                   ag.aggfinalfn::text AS ffunc, ag.agginitval AS initcond
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            JOIN pg_language l ON l.oid = p.prolang
            LEFT JOIN pg_aggregate ag ON ag.aggfnoid = p.oid
            WHERE n.nspname = @s AND p.proname = @n AND pg_get_function_identity_arguments(p.oid) = @sig
            """,
            ct,
            ("@s", node.Schema),
            ("@n", node.Name),
            ("@sig", node.Signature ?? string.Empty)).ConfigureAwait(false);

        var signature = $"{Q(node.Schema!)}.{Q(node.Name!)}({node.Signature})";
        string? sql;
        if (node.Kind == NodeKind.Aggregate)
        {
            var body = new StringBuilder()
                .AppendLine($"-- Aggregate: {node.Schema}.{node.Name}({node.Signature})")
                .AppendLine()
                .AppendLine($"CREATE OR REPLACE AGGREGATE {signature} (")
                .Append($"    SFUNC = {Get(row, "sfunc")},").AppendLine()
                .Append($"    STYPE = {Get(row, "stype")}");
            if (Get(row, "ffunc") is { } ffunc && ffunc != "-")
            {
                body.Append(',').AppendLine().Append($"    FINALFUNC = {ffunc}");
            }

            if (Get(row, "initcond") is { } init)
            {
                body.Append(',').AppendLine().Append($"    INITCOND = {Lit(init)}");
            }

            sql = body.AppendLine().Append(");").ToString();
        }
        else
        {
            var kindWord = node.Kind == NodeKind.Procedure ? "PROCEDURE" : "FUNCTION";
            sql = new StringBuilder()
                .AppendLine($"-- {(node.Kind == NodeKind.Procedure ? "PROCEDURE" : "FUNCTION")}: {node.Schema}.{node.Name}({node.Signature})")
                .AppendLine()
                .AppendLine($"-- DROP {kindWord} IF EXISTS {signature};")
                .AppendLine()
                .AppendLine((Get(row, "definition") ?? string.Empty).TrimEnd())
                .AppendLine()
                .AppendLine($"ALTER {kindWord} {signature}")
                .Append($"    OWNER TO {Q(Get(row, "owner") ?? "postgres")};")
                .Append(CommentOn(kindWord, signature, Get(row, "comment")))
                .ToString();
        }

        return new ObjectProperties
        {
            Title = $"{node.Name}({node.Signature})",
            Properties = Props(
                ("Name", node.Name),
                ("Schema", node.Schema),
                ("Owner", Get(row, "owner")),
                ("Arguments", Get(row, "arguments")),
                ("Returns", Get(row, "returns")),
                ("Language", Get(row, "language")),
                ("Volatility", node.Kind == NodeKind.Procedure ? null : Get(row, "volatility")),
                ("Parallel", node.Kind == NodeKind.Procedure ? null : Get(row, "parallel")),
                ("Strict", Get(row, "strict")),
                ("Security definer", Get(row, "security_definer")),
                ("Returns a set", Get(row, "returns_set")),
                ("Estimated cost", Get(row, "cost")),
                ("Estimated rows", Get(row, "rows")),
                ("Comment", Get(row, "comment"))),
            Sql = sql,
        };
    }

    private async Task<ObjectProperties> ColumnPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            $"""
            SELECT format_type(a.atttypid, a.atttypmod) AS type, a.attnotnull AS notnull,
                   pg_get_expr(d.adbin, d.adrelid) AS "default", a.attidentity::text AS identity,
                   a.attgenerated::text AS generated, co.collname AS collation, a.attnum AS position,
                   col_description(a.attrelid, a.attnum) AS comment
            FROM pg_attribute a
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            LEFT JOIN pg_collation co ON co.oid = a.attcollation AND a.attcollation <> 0 AND co.collname <> 'default'
            WHERE a.attrelid = {RelationOid} AND a.attname = @n AND NOT a.attisdropped
            """,
            ct,
            ("@s", node.Schema),
            ("@r", node.Relation),
            ("@n", node.Name)).ConfigureAwait(false);

        var table = $"{Q(node.Schema!)}.{Q(node.Relation!)}";
        var identity = Get(row, "identity") switch
        {
            "a" => "GENERATED ALWAYS AS IDENTITY",
            "d" => "GENERATED BY DEFAULT AS IDENTITY",
            _ => null,
        };
        var generated = Get(row, "generated") == "s";
        var clause = new StringBuilder($"{Q(node.Name!)} {Get(row, "type")}");
        if (Get(row, "collation") is { } collation)
        {
            clause.Append($" COLLATE {Q(collation)}");
        }

        if (Get(row, "notnull") == "Yes")
        {
            clause.Append(" NOT NULL");
        }

        if (identity is not null)
        {
            clause.Append(' ').Append(identity);
        }
        else if (generated)
        {
            clause.Append($" GENERATED ALWAYS AS ({Get(row, "default")}) STORED");
        }
        else if (Get(row, "default") is { } dflt)
        {
            clause.Append($" DEFAULT {dflt}");
        }

        var sql = new StringBuilder()
            .AppendLine($"-- Column: {node.Schema}.{node.Relation}.{node.Name}")
            .AppendLine()
            .AppendLine($"-- ALTER TABLE IF EXISTS {table} DROP COLUMN IF EXISTS {Q(node.Name!)};")
            .AppendLine()
            .AppendLine($"ALTER TABLE IF EXISTS {table}")
            .Append($"    ADD COLUMN {clause};")
            .Append(CommentOn("COLUMN", $"{table}.{Q(node.Name!)}", Get(row, "comment")));

        return new ObjectProperties
        {
            Title = $"{node.Relation}.{node.Name}",
            Properties = Props(
                ("Name", node.Name),
                ("Position", Get(row, "position")),
                ("Data type", Get(row, "type")),
                ("Not NULL", Get(row, "notnull")),
                ("Default", generated ? null : Get(row, "default")),
                ("Identity", identity),
                ("Generated", generated ? Get(row, "default") : null),
                ("Collation", Get(row, "collation")),
                ("Comment", Get(row, "comment"))),
            Sql = sql.ToString(),
        };
    }

    private async Task<ObjectProperties> ConstraintPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            $"""
            SELECT pg_get_constraintdef(k.oid, true) AS definition, k.condeferrable AS deferrable,
                   k.condeferred AS deferred, k.convalidated AS validated,
                   obj_description(k.oid, 'pg_constraint') AS comment,
                   CASE WHEN k.contype = 'f' THEN k.confrelid::regclass::text END AS "references",
                   CASE WHEN k.conindid <> 0 THEN k.conindid::regclass::text END AS "index"
            FROM pg_constraint k
            WHERE k.conrelid = {RelationOid} AND k.conname = @n
            """,
            ct,
            ("@s", node.Schema),
            ("@r", node.Relation),
            ("@n", node.Name)).ConfigureAwait(false);

        var table = $"{Q(node.Schema!)}.{Q(node.Relation!)}";
        var sql = new StringBuilder()
            .AppendLine($"-- Constraint: {node.Name}")
            .AppendLine()
            .AppendLine($"-- ALTER TABLE IF EXISTS {table} DROP CONSTRAINT IF EXISTS {Q(node.Name!)};")
            .AppendLine()
            .AppendLine($"ALTER TABLE IF EXISTS {table}")
            .Append($"    ADD CONSTRAINT {Q(node.Name!)} {Get(row, "definition")};")
            .Append(CommentOn("CONSTRAINT", $"{Q(node.Name!)} ON {table}", Get(row, "comment")));

        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(
                ("Name", node.Name),
                ("Type", node.Kind switch
                {
                    NodeKind.PrimaryKey => "Primary key",
                    NodeKind.ForeignKey => "Foreign key",
                    NodeKind.UniqueConstraint => "Unique",
                    NodeKind.CheckConstraint => "Check",
                    _ => "Exclusion",
                }),
                ("Definition", Get(row, "definition")),
                ("References", Get(row, "references")),
                ("Index", Get(row, "index")),
                ("Deferrable", Get(row, "deferrable")),
                ("Deferred", Get(row, "deferred")),
                ("Validated", Get(row, "validated")),
                ("Comment", Get(row, "comment"))),
            Sql = sql.ToString(),
        };
    }

    private async Task<ObjectProperties> IndexPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            $"""
            SELECT pg_get_indexdef(ic.oid) AS definition, ix.indisunique AS "unique", ix.indisprimary AS "primary",
                   ix.indisvalid AS valid, ix.indisclustered AS clustered, am.amname AS method,
                   pg_size_pretty(pg_relation_size(ic.oid)) AS size, obj_description(ic.oid, 'pg_class') AS comment,
                   COALESCE(t.spcname, 'pg_default') AS tablespace
            FROM pg_class ic
            JOIN pg_index ix ON ix.indexrelid = ic.oid
            JOIN pg_am am ON am.oid = ic.relam
            LEFT JOIN pg_tablespace t ON t.oid = ic.reltablespace
            WHERE ic.oid = {ObjectOid}
            """,
            ct,
            ("@s", node.Schema),
            ("@n", node.Name)).ConfigureAwait(false);

        var sql = new StringBuilder()
            .AppendLine($"-- Index: {node.Name}")
            .AppendLine()
            .AppendLine($"-- DROP INDEX IF EXISTS {Q(node.Schema!)}.{Q(node.Name!)};")
            .AppendLine()
            .Append($"{Get(row, "definition")};")
            .Append(CommentOn("INDEX", $"{Q(node.Schema!)}.{Q(node.Name!)}", Get(row, "comment")));

        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(
                ("Name", node.Name),
                ("Table", node.Relation),
                ("Access method", Get(row, "method")),
                ("Unique", Get(row, "unique")),
                ("Clustered", Get(row, "clustered")),
                ("Valid", Get(row, "valid")),
                ("Size", Get(row, "size")),
                ("Tablespace", Get(row, "tablespace")),
                ("Definition", Get(row, "definition")),
                ("Comment", Get(row, "comment"))),
            Sql = sql.ToString(),
        };
    }

    private async Task<ObjectProperties> TriggerPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            $"""
            SELECT pg_get_triggerdef(t.oid, true) AS definition, t.tgenabled::text AS enabled,
                   t.tgfoid::regproc::text AS function, obj_description(t.oid, 'pg_trigger') AS comment
            FROM pg_trigger t
            WHERE t.tgrelid = {RelationOid} AND t.tgname = @n AND NOT t.tgisinternal
            """,
            ct,
            ("@s", node.Schema),
            ("@r", node.Relation),
            ("@n", node.Name)).ConfigureAwait(false);

        var table = $"{Q(node.Schema!)}.{Q(node.Relation!)}";
        var sql = new StringBuilder()
            .AppendLine($"-- Trigger: {node.Name}")
            .AppendLine()
            .AppendLine($"-- DROP TRIGGER IF EXISTS {Q(node.Name!)} ON {table};")
            .AppendLine()
            .Append($"{Get(row, "definition")};")
            .Append(CommentOn("TRIGGER", $"{Q(node.Name!)} ON {table}", Get(row, "comment")));

        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(
                ("Name", node.Name),
                ("Table", node.Relation),
                ("Function", Get(row, "function")),
                ("Enabled", Get(row, "enabled") switch { "D" => "Disabled", "R" => "Replica", "A" => "Always", _ => "Enabled" }),
                ("Comment", Get(row, "comment"))),
            Sql = sql.ToString(),
        };
    }

    private async Task<ObjectProperties> RolePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT r.rolsuper, r.rolinherit, r.rolcreaterole, r.rolcreatedb, r.rolcanlogin, r.rolreplication,
                   r.rolbypassrls, r.rolconnlimit, r.rolvaliduntil::text AS validuntil,
                   shobj_description(r.oid, 'pg_authid') AS comment,
                   (SELECT string_agg(g.rolname, ', ' ORDER BY g.rolname)
                      FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid WHERE m.member = r.oid) AS member_of
            FROM pg_roles r WHERE r.rolname = @n
            """,
            ct,
            ("@n", node.Name)).ConfigureAwait(false);

        string Flag(string column, string on, string off) => Get(row, column) == "Yes" ? on : off;
        var name = node.Name!;
        var sql = new StringBuilder()
            .AppendLine($"-- Role: {name}")
            .AppendLine($"-- DROP ROLE IF EXISTS {Q(name)};")
            .AppendLine()
            .AppendLine($"CREATE ROLE {Q(name)} WITH")
            .AppendLine($"  {Flag("rolcanlogin", "LOGIN", "NOLOGIN")}")
            .AppendLine($"  {Flag("rolsuper", "SUPERUSER", "NOSUPERUSER")}")
            .AppendLine($"  {Flag("rolinherit", "INHERIT", "NOINHERIT")}")
            .AppendLine($"  {Flag("rolcreatedb", "CREATEDB", "NOCREATEDB")}")
            .AppendLine($"  {Flag("rolcreaterole", "CREATEROLE", "NOCREATEROLE")}")
            .AppendLine($"  {Flag("rolreplication", "REPLICATION", "NOREPLICATION")}")
            .Append($"  {Flag("rolbypassrls", "BYPASSRLS", "NOBYPASSRLS")}");
        if (Get(row, "rolconnlimit") is { } limit && limit != "-1")
        {
            sql.AppendLine().Append($"  CONNECTION LIMIT {limit}");
        }

        if (Get(row, "validuntil") is { } until)
        {
            sql.AppendLine().Append($"  VALID UNTIL {Lit(until)}");
        }

        sql.Append(';');
        if (Get(row, "member_of") is { } groups)
        {
            sql.AppendLine().AppendLine();
            foreach (var group in groups.Split(", "))
            {
                sql.AppendLine($"GRANT {Q(group)} TO {Q(name)};");
            }
        }

        sql.Append(CommentOn("ROLE", Q(name), Get(row, "comment")));

        return new ObjectProperties
        {
            Title = name,
            Properties = Props(
                ("Name", name),
                ("Can login", Get(row, "rolcanlogin")),
                ("Superuser", Get(row, "rolsuper")),
                ("Create roles", Get(row, "rolcreaterole")),
                ("Create databases", Get(row, "rolcreatedb")),
                ("Inherit rights", Get(row, "rolinherit")),
                ("Replication", Get(row, "rolreplication")),
                ("Bypass RLS", Get(row, "rolbypassrls")),
                ("Connection limit", Get(row, "rolconnlimit")),
                ("Account expires", Get(row, "validuntil") ?? "No expiry"),
                ("Member of", Get(row, "member_of")),
                ("Comment", Get(row, "comment"))),
            Sql = sql.ToString(),
        };
    }

    private async Task<ObjectProperties> TablespacePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT pg_get_userbyid(spcowner) AS owner, pg_tablespace_location(oid) AS location,
                   shobj_description(oid, 'pg_tablespace') AS comment, array_to_string(spcoptions, ', ') AS options
            FROM pg_tablespace WHERE spcname = @n
            """,
            ct,
            ("@n", node.Name)).ConfigureAwait(false);
        var size = await TryScalarAsync("SELECT pg_size_pretty(pg_tablespace_size(@n::name))", ct, ("@n", node.Name)).ConfigureAwait(false);

        var location = Get(row, "location");
        var sql = string.IsNullOrEmpty(location)
            ? $"-- Tablespace: {node.Name}\n\n-- {node.Name} is a built-in tablespace."
            : $"-- Tablespace: {node.Name}\n\n-- DROP TABLESPACE IF EXISTS {Q(node.Name!)};\n\nCREATE TABLESPACE {Q(node.Name!)}\n  OWNER {Q(Get(row, "owner") ?? "postgres")}\n  LOCATION {Lit(location)};";

        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(("Name", node.Name), ("Owner", Get(row, "owner")), ("Location", string.IsNullOrEmpty(location) ? "(built-in)" : location), ("Size", Text(size)), ("Options", Get(row, "options")), ("Comment", Get(row, "comment"))),
            Sql = sql + CommentOn("TABLESPACE", Q(node.Name!), Get(row, "comment")),
        };
    }

    private async Task<ObjectProperties> ExtensionPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT e.extversion AS version, n.nspname AS schema, e.extrelocatable AS relocatable,
                   pg_get_userbyid(e.extowner) AS owner,
                   (SELECT comment FROM pg_available_extensions WHERE name = e.extname) AS comment
            FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = @n
            """,
            ct,
            ("@n", node.Name)).ConfigureAwait(false);

        var sql = $"-- Extension: {node.Name}\n\n-- DROP EXTENSION {Q(node.Name!)};\n\nCREATE EXTENSION IF NOT EXISTS {Q(node.Name!)}\n    SCHEMA {Q(Get(row, "schema") ?? "public")}\n    VERSION {Q(Get(row, "version") ?? string.Empty)};";
        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(("Name", node.Name), ("Version", Get(row, "version")), ("Schema", Get(row, "schema")), ("Owner", Get(row, "owner")), ("Relocatable", Get(row, "relocatable")), ("Comment", Get(row, "comment"))),
            Sql = sql,
        };
    }

    private async Task<ObjectProperties> LanguagePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT pg_get_userbyid(lanowner) AS owner, lanpltrusted AS trusted, lanplcallfoid::regproc::text AS handler,
                   laninline::regproc::text AS inline_handler, lanvalidator::regproc::text AS validator
            FROM pg_language WHERE lanname = @n
            """,
            ct,
            ("@n", node.Name)).ConfigureAwait(false);

        var trusted = Get(row, "trusted") == "Yes";
        var sql = new StringBuilder()
            .AppendLine($"-- Language: {node.Name}")
            .AppendLine()
            .AppendLine($"-- DROP LANGUAGE IF EXISTS {Q(node.Name!)}")
            .AppendLine()
            .AppendLine($"CREATE OR REPLACE {(trusted ? "TRUSTED " : string.Empty)}PROCEDURAL LANGUAGE {Q(node.Name!)}")
            .AppendLine($"    HANDLER {Get(row, "handler")}")
            .AppendLine($"    INLINE {Get(row, "inline_handler")}")
            .Append($"    VALIDATOR {Get(row, "validator")};");

        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(("Name", node.Name), ("Owner", Get(row, "owner")), ("Trusted", Get(row, "trusted")), ("Handler", Get(row, "handler")), ("Inline handler", Get(row, "inline_handler")), ("Validator", Get(row, "validator"))),
            Sql = sql.ToString(),
        };
    }

    private async Task<ObjectProperties> CastPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT format_type(c.castsource, NULL) AS source, format_type(c.casttarget, NULL) AS target,
                   CASE WHEN c.castfunc <> 0 THEN c.castfunc::regprocedure::text END AS function,
                   c.castcontext::text AS context, c.castmethod::text AS method
            FROM pg_cast c WHERE c.oid = @oid::oid
            """,
            ct,
            ("@oid", node.Signature)).ConfigureAwait(false);

        var with = Get(row, "method") switch
        {
            "b" => "WITHOUT FUNCTION",
            "i" => "WITH INOUT",
            _ => $"WITH FUNCTION {Get(row, "function")}",
        };
        var context = Get(row, "context") switch
        {
            "a" => "\n    AS ASSIGNMENT",
            "i" => "\n    AS IMPLICIT",
            _ => string.Empty,
        };
        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(
                ("Source type", Get(row, "source")),
                ("Target type", Get(row, "target")),
                ("Function", Get(row, "function")),
                ("Context", Get(row, "context") switch { "a" => "ASSIGNMENT", "i" => "IMPLICIT", _ => "EXPLICIT" })),
            Sql = $"-- Cast: {node.Name}\n\nCREATE CAST ({Get(row, "source")} AS {Get(row, "target")})\n    {with}{context};",
        };
    }

    private async Task<ObjectProperties> EventTriggerPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT evtevent AS event, pg_get_userbyid(evtowner) AS owner, evtfoid::regproc::text AS function,
                   evtenabled::text AS enabled, array_to_string(evttags, ''', ''') AS tags,
                   obj_description(oid, 'pg_event_trigger') AS comment
            FROM pg_event_trigger WHERE evtname = @n
            """,
            ct,
            ("@n", node.Name)).ConfigureAwait(false);

        var tags = Get(row, "tags");
        var sql = $"-- Event Trigger: {node.Name}\n\nCREATE EVENT TRIGGER {Q(node.Name!)} ON {Get(row, "event")}" +
                  (string.IsNullOrEmpty(tags) ? string.Empty : $"\n    WHEN TAG IN ('{tags}')") +
                  $"\n    EXECUTE FUNCTION {Get(row, "function")}();";
        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(("Name", node.Name), ("Event", Get(row, "event")), ("Function", Get(row, "function")), ("Owner", Get(row, "owner")), ("Enabled", Get(row, "enabled") == "D" ? "Disabled" : "Enabled"), ("Tags", tags), ("Comment", Get(row, "comment"))),
            Sql = sql + CommentOn("EVENT TRIGGER", Q(node.Name!), Get(row, "comment")),
        };
    }

    private async Task<ObjectProperties> ForeignDataWrapperPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT pg_get_userbyid(fdwowner) AS owner, fdwhandler::regproc::text AS handler,
                   fdwvalidator::regproc::text AS validator, array_to_string(fdwoptions, ', ') AS options,
                   obj_description(oid, 'pg_foreign_data_wrapper') AS comment
            FROM pg_foreign_data_wrapper WHERE fdwname = @n
            """,
            ct,
            ("@n", node.Name)).ConfigureAwait(false);

        var sql = new StringBuilder($"-- Foreign Data Wrapper: {node.Name}\n\nCREATE FOREIGN DATA WRAPPER {Q(node.Name!)}");
        if (Get(row, "handler") is { } handler && handler != "-")
        {
            sql.Append($"\n    HANDLER {handler}");
        }

        if (Get(row, "validator") is { } validator && validator != "-")
        {
            sql.Append($"\n    VALIDATOR {validator}");
        }

        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(("Name", node.Name), ("Owner", Get(row, "owner")), ("Handler", Get(row, "handler")), ("Validator", Get(row, "validator")), ("Options", Get(row, "options")), ("Comment", Get(row, "comment"))),
            Sql = sql.Append(';').ToString(),
        };
    }

    private async Task<ObjectProperties> ForeignServerPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT pg_get_userbyid(s.srvowner) AS owner, s.srvtype AS type, s.srvversion AS version,
                   s.srvoptions AS options, w.fdwname AS fdw
            FROM pg_foreign_server s JOIN pg_foreign_data_wrapper w ON w.oid = s.srvfdw WHERE s.srvname = @n
            """,
            ct,
            ("@n", node.Name)).ConfigureAwait(false);

        var options = row?["options"] as string[] ?? [];
        var sql = new StringBuilder($"-- Foreign Server: {node.Name}\n\nCREATE SERVER {Q(node.Name!)}");
        if (Get(row, "type") is { } type)
        {
            sql.Append($"\n    TYPE {Lit(type)}");
        }

        if (Get(row, "version") is { } version)
        {
            sql.Append($"\n    VERSION {Lit(version)}");
        }

        sql.Append($"\n    FOREIGN DATA WRAPPER {Q(Get(row, "fdw") ?? string.Empty)}");
        if (options.Length > 0)
        {
            sql.Append("\n    OPTIONS (").Append(string.Join(", ", options.Select(OptionClause))).Append(')');
        }

        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(("Name", node.Name), ("Wrapper", Get(row, "fdw")), ("Owner", Get(row, "owner")), ("Type", Get(row, "type")), ("Version", Get(row, "version")), ("Options", string.Join(", ", options))),
            Sql = sql.Append(';').ToString(),
        };
    }

    private async Task<ObjectProperties> PublicationPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT pg_get_userbyid(p.pubowner) AS owner, p.puballtables AS all_tables, p.pubinsert AS ins,
                   p.pubupdate AS upd, p.pubdelete AS del, p.pubtruncate AS trunc,
                   (SELECT string_agg(format('%I.%I', schemaname, tablename), ', ') FROM pg_publication_tables t WHERE t.pubname = p.pubname) AS tables
            FROM pg_publication p WHERE p.pubname = @n
            """,
            ct,
            ("@n", node.Name)).ConfigureAwait(false);

        var publish = new[] { ("ins", "insert"), ("upd", "update"), ("del", "delete"), ("trunc", "truncate") }
            .Where(p => Get(row, p.Item1) == "Yes").Select(p => p.Item2);
        var target = Get(row, "all_tables") == "Yes" ? "FOR ALL TABLES" : Get(row, "tables") is { } tables ? $"FOR TABLE {tables}" : string.Empty;
        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(("Name", node.Name), ("Owner", Get(row, "owner")), ("All tables", Get(row, "all_tables")), ("Tables", Get(row, "tables")), ("Publishes", string.Join(", ", publish))),
            Sql = $"-- Publication: {node.Name}\n\nCREATE PUBLICATION {Q(node.Name!)}\n    {target}\n    WITH (publish = '{string.Join(", ", publish)}');",
        };
    }

    private async Task<ObjectProperties> SubscriptionPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT pg_get_userbyid(s.subowner) AS owner, s.subenabled AS enabled,
                   array_to_string(s.subpublications, ', ') AS publications, s.subslotname AS slot
            FROM pg_subscription s
            WHERE s.subname = @n AND s.subdbid = (SELECT oid FROM pg_database WHERE datname = current_database())
            """,
            ct,
            ("@n", node.Name)).ConfigureAwait(false);

        return new ObjectProperties
        {
            Title = node.Name!,
            Properties = Props(("Name", node.Name), ("Owner", Get(row, "owner")), ("Enabled", Get(row, "enabled")), ("Publications", Get(row, "publications")), ("Replication slot", Get(row, "slot"))),
            Sql = $"-- Subscription: {node.Name}\n\n-- The connection string is not readable from the catalog.\nCREATE SUBSCRIPTION {Q(node.Name!)}\n    CONNECTION '...'\n    PUBLICATION {Get(row, "publications")};",
        };
    }

    private async Task<ObjectProperties> TypePropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT pg_get_userbyid(t.typowner) AS owner, t.typtype::text AS kind, obj_description(t.oid, 'pg_type') AS comment,
                   (SELECT string_agg(quote_literal(e.enumlabel), ', ' ORDER BY e.enumsortorder) FROM pg_enum e WHERE e.enumtypid = t.oid) AS labels,
                   (SELECT string_agg(format('%I %s', a.attname, format_type(a.atttypid, a.atttypmod)), E',\n    ' ORDER BY a.attnum)
                      FROM pg_attribute a WHERE a.attrelid = t.typrelid AND a.attnum > 0 AND NOT a.attisdropped) AS attributes,
                   (SELECT format_type(r.rngsubtype, NULL) FROM pg_range r WHERE r.rngtypid = t.oid) AS subtype
            FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = @s AND t.typname = @n
            """,
            ct,
            ("@s", node.Schema),
            ("@n", node.Name)).ConfigureAwait(false);

        var qualified = $"{Q(node.Schema!)}.{Q(node.Name!)}";
        var kind = Get(row, "kind");
        var body = kind switch
        {
            "e" => $"CREATE TYPE {qualified} AS ENUM\n    ({Get(row, "labels")});",
            "c" => $"CREATE TYPE {qualified} AS\n(\n    {Get(row, "attributes")}\n);",
            "r" => $"CREATE TYPE {qualified} AS RANGE\n(\n    SUBTYPE={Get(row, "subtype")}\n);",
            _ => $"-- {qualified} is a base type defined in C; its definition is not shown.",
        };
        return new ObjectProperties
        {
            Title = $"{node.Schema}.{node.Name}",
            Properties = Props(
                ("Name", node.Name),
                ("Schema", node.Schema),
                ("Owner", Get(row, "owner")),
                ("Kind", kind switch { "e" => "Enumeration", "c" => "Composite", "r" => "Range", _ => "Base" }),
                ("Labels", Get(row, "labels")),
                ("Subtype", Get(row, "subtype")),
                ("Comment", Get(row, "comment"))),
            Sql = $"-- Type: {node.Name}\n\n-- DROP TYPE IF EXISTS {qualified};\n\n{body}" + CommentOn("TYPE", qualified, Get(row, "comment")),
        };
    }

    private async Task<ObjectProperties> DomainPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT pg_get_userbyid(t.typowner) AS owner, format_type(t.typbasetype, t.typtypmod) AS base,
                   t.typnotnull AS notnull, t.typdefault AS dflt, obj_description(t.oid, 'pg_type') AS comment,
                   (SELECT string_agg(format('CONSTRAINT %I %s', c.conname, pg_get_constraintdef(c.oid, true)), E'\n    ')
                      FROM pg_constraint c WHERE c.contypid = t.oid) AS checks
            FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE n.nspname = @s AND t.typname = @n AND t.typtype = 'd'
            """,
            ct,
            ("@s", node.Schema),
            ("@n", node.Name)).ConfigureAwait(false);

        var qualified = $"{Q(node.Schema!)}.{Q(node.Name!)}";
        var sql = new StringBuilder($"-- DOMAIN: {node.Schema}.{node.Name}\n\n-- DROP DOMAIN IF EXISTS {qualified};\n\nCREATE DOMAIN {qualified}\n    AS {Get(row, "base")}");
        if (Get(row, "dflt") is { } dflt)
        {
            sql.Append($"\n    DEFAULT {dflt}");
        }

        if (Get(row, "notnull") == "Yes")
        {
            sql.Append("\n    NOT NULL");
        }

        if (Get(row, "checks") is { } checks)
        {
            sql.Append($"\n    {checks}");
        }

        return new ObjectProperties
        {
            Title = $"{node.Schema}.{node.Name}",
            Properties = Props(("Name", node.Name), ("Base type", Get(row, "base")), ("Owner", Get(row, "owner")), ("Not NULL", Get(row, "notnull")), ("Default", Get(row, "dflt")), ("Constraints", Get(row, "checks")), ("Comment", Get(row, "comment"))),
            Sql = sql.Append(';').Append(CommentOn("DOMAIN", qualified, Get(row, "comment"))).ToString(),
        };
    }

    private async Task<ObjectProperties> CollationPropertiesAsync(NodeRef node, CancellationToken ct)
    {
        var row = await RowAsync(
            """
            SELECT pg_get_userbyid(c.collowner) AS owner,
                   CASE c.collprovider WHEN 'c' THEN 'libc' WHEN 'i' THEN 'icu' WHEN 'b' THEN 'builtin' ELSE 'default' END AS provider,
                   c.collcollate AS lc_collate, c.collctype AS lc_ctype, c.collisdeterministic AS deterministic
            FROM pg_collation c JOIN pg_namespace n ON n.oid = c.collnamespace
            WHERE n.nspname = @s AND c.collname = @n
            LIMIT 1
            """,
            ct,
            ("@s", node.Schema),
            ("@n", node.Name)).ConfigureAwait(false);

        var qualified = $"{Q(node.Schema!)}.{Q(node.Name!)}";
        return new ObjectProperties
        {
            Title = $"{node.Schema}.{node.Name}",
            Properties = Props(("Name", node.Name), ("Owner", Get(row, "owner")), ("Provider", Get(row, "provider")), ("LC_COLLATE", Get(row, "lc_collate")), ("LC_CTYPE", Get(row, "lc_ctype")), ("Deterministic", Get(row, "deterministic"))),
            Sql = $"-- Collation: {node.Name};\n\nCREATE COLLATION IF NOT EXISTS {qualified}\n    (PROVIDER = {Get(row, "provider")}" +
                  (Get(row, "lc_collate") is { } collate ? $", LC_COLLATE = {Lit(collate)}" : string.Empty) +
                  (Get(row, "lc_ctype") is { } ctype ? $", LC_CTYPE = {Lit(ctype)}" : string.Empty) +
                  $", DETERMINISTIC = {(Get(row, "deterministic") == "No" ? "false" : "true")});",
        };
    }

    // ------------------------------------------------------------------- helpers

    /// <summary>Quotes an identifier the way <c>quote_ident</c> does: only when it is not a plain lower-case, non-reserved name.</summary>
    /// <param name="name">The identifier.</param>
    /// <returns>The identifier, quoted when necessary.</returns>
    internal static string Q(string name) =>
        PlainIdentifier().IsMatch(name) && !Reserved.Contains(name)
            ? name
            : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Lit(string? value) =>
        value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string CommentOn(string objectType, string target, string? comment) =>
        comment is null ? string.Empty : $"\n\nCOMMENT ON {objectType} {target}\n    IS {Lit(comment)};";

    private static string OptionClause(string option)
    {
        var equals = option.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? option : $"{option[..equals]} {Lit(option[(equals + 1)..])}";
    }

    [GeneratedRegex("^[a-z_][a-z0-9_$]*$")]
    private static partial Regex PlainIdentifier();
}
