namespace Bengine.Enums;

/// <summary>
/// Identifies a concrete relational database management system that the schema
/// framework can target. The AST itself is provider-independent; providers are
/// only referenced when mapping, generating, or validating provider-specific SQL.
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
}
