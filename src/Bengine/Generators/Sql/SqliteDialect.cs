namespace Bengine.Generators.Sql;

using Bengine.Enums;
using Bengine.Models.Options;

/// <summary>SQLite syntax tokens for <see cref="SqlGeneratorBase"/>.</summary>
public sealed class SqliteDialect : SqlDialect
{
    /// <inheritdoc />
    public override DatabaseProvider Provider => DatabaseProvider.Sqlite;

    /// <inheritdoc />
    public override SqlDialectFeature Features =>
        SqlDialectFeature.IfNotExists
        | SqlDialectFeature.CheckConstraints
        | SqlDialectFeature.PartialIndexes
        | SqlDialectFeature.GeneratedColumns;

    /// <inheritdoc />
    public override string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    /// <inheritdoc />
    public override string RenderIdentity(IdentityOptions identity) =>
        // SQLite auto-populates INTEGER PRIMARY KEY rows implicitly; no inline
        // identity clause is emitted.
        string.Empty;
}
