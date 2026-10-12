namespace Bengine.Introspection.Tree;

/// <summary>
/// The kind of a node in the object explorer tree. Every collection has a
/// <c>…Folder</c> kind (for example <see cref="TablesFolder"/>) whose children are
/// items of the matching object kind (<see cref="Table"/>). The set follows
/// pgAdmin's PostgreSQL hierarchy; the other providers use the subset that
/// applies to them.
/// </summary>
public enum NodeKind
{
    /// <summary>A registered server.</summary>
    Server,

    /// <summary>The databases on a server.</summary>
    DatabasesFolder,

    /// <summary>A database.</summary>
    Database,

    /// <summary>The login and group roles of a server (MySQL users, SQL Server logins).</summary>
    RolesFolder,

    /// <summary>A role that can log in.</summary>
    LoginRole,

    /// <summary>A role that cannot log in (a group).</summary>
    GroupRole,

    /// <summary>The tablespaces of a server.</summary>
    TablespacesFolder,

    /// <summary>A tablespace.</summary>
    Tablespace,

    /// <summary>The user-defined casts of a database.</summary>
    CastsFolder,

    /// <summary>A cast.</summary>
    Cast,

    /// <summary>The system catalogs of a database.</summary>
    CatalogsFolder,

    /// <summary>A system catalog (<c>pg_catalog</c>, <c>information_schema</c>).</summary>
    Catalog,

    /// <summary>The event triggers of a database.</summary>
    EventTriggersFolder,

    /// <summary>An event trigger.</summary>
    EventTrigger,

    /// <summary>The installed extensions of a database.</summary>
    ExtensionsFolder,

    /// <summary>An extension.</summary>
    Extension,

    /// <summary>The foreign-data wrappers of a database.</summary>
    ForeignDataWrappersFolder,

    /// <summary>A foreign-data wrapper.</summary>
    ForeignDataWrapper,

    /// <summary>The foreign servers of a foreign-data wrapper.</summary>
    ForeignServersFolder,

    /// <summary>A foreign server.</summary>
    ForeignServer,

    /// <summary>The procedural languages of a database.</summary>
    LanguagesFolder,

    /// <summary>A procedural language.</summary>
    Language,

    /// <summary>The logical-replication publications of a database.</summary>
    PublicationsFolder,

    /// <summary>A publication.</summary>
    Publication,

    /// <summary>The schemas of a database.</summary>
    SchemasFolder,

    /// <summary>A schema.</summary>
    Schema,

    /// <summary>The logical-replication subscriptions of a database.</summary>
    SubscriptionsFolder,

    /// <summary>A subscription.</summary>
    Subscription,

    /// <summary>The aggregate functions of a schema.</summary>
    AggregatesFolder,

    /// <summary>An aggregate function.</summary>
    Aggregate,

    /// <summary>The collations of a schema.</summary>
    CollationsFolder,

    /// <summary>A collation.</summary>
    Collation,

    /// <summary>The domains of a schema.</summary>
    DomainsFolder,

    /// <summary>A domain.</summary>
    Domain,

    /// <summary>The foreign tables of a schema.</summary>
    ForeignTablesFolder,

    /// <summary>A foreign table.</summary>
    ForeignTable,

    /// <summary>The functions of a schema.</summary>
    FunctionsFolder,

    /// <summary>A function.</summary>
    Function,

    /// <summary>The materialized views of a schema.</summary>
    MaterializedViewsFolder,

    /// <summary>A materialized view.</summary>
    MaterializedView,

    /// <summary>The procedures of a schema.</summary>
    ProceduresFolder,

    /// <summary>A procedure.</summary>
    Procedure,

    /// <summary>The sequences of a schema.</summary>
    SequencesFolder,

    /// <summary>A sequence.</summary>
    Sequence,

    /// <summary>The tables of a schema.</summary>
    TablesFolder,

    /// <summary>A table.</summary>
    Table,

    /// <summary>The trigger functions of a schema.</summary>
    TriggerFunctionsFolder,

    /// <summary>A trigger function.</summary>
    TriggerFunction,

    /// <summary>The types of a schema.</summary>
    TypesFolder,

    /// <summary>A type.</summary>
    Type,

    /// <summary>The views of a schema.</summary>
    ViewsFolder,

    /// <summary>A view.</summary>
    View,

    /// <summary>The scheduled events of a database (MySQL).</summary>
    EventsFolder,

    /// <summary>A scheduled event (MySQL).</summary>
    Event,

    /// <summary>The columns of a relation.</summary>
    ColumnsFolder,

    /// <summary>A column.</summary>
    Column,

    /// <summary>The constraints of a table.</summary>
    ConstraintsFolder,

    /// <summary>A primary-key constraint.</summary>
    PrimaryKey,

    /// <summary>A foreign-key constraint.</summary>
    ForeignKey,

    /// <summary>A unique constraint.</summary>
    UniqueConstraint,

    /// <summary>A check constraint.</summary>
    CheckConstraint,

    /// <summary>An exclusion constraint.</summary>
    ExclusionConstraint,

    /// <summary>The indexes of a relation.</summary>
    IndexesFolder,

    /// <summary>An index.</summary>
    Index,

    /// <summary>The triggers of a relation.</summary>
    TriggersFolder,

    /// <summary>A trigger.</summary>
    Trigger,
}
