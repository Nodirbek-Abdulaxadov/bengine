namespace Bengine.Enums;

/// <summary>
/// Identifies a concrete database management system. The relational providers
/// are the ones the schema framework can target; the AST itself is
/// provider-independent, and providers are only referenced when mapping,
/// generating, or validating provider-specific SQL. <see cref="MongoDb"/> is a
/// document database: only the live-database layer speaks to it, and it is not
/// a target for SQL generation (see <c>SchemaConstants.AllProviders</c>).
/// </summary>
public enum DatabaseProvider
{
    /// <summary>PostgreSQL.</summary>
    PostgreSql,

    /// <summary>Microsoft SQL Server.</summary>
    SqlServer,

    /// <summary>MySQL.</summary>
    MySql,

    /// <summary>SQLite.</summary>
    Sqlite,

    /// <summary>Oracle Database (12c and later).</summary>
    Oracle,

    /// <summary>MongoDB, a document database: browsing and its shell only, no SQL generation.</summary>
    MongoDb,
}
