namespace Bengine.Generators.Sql;

using Bengine.Enums;
using Bengine.Models.Options;

/// <summary>SQL Server syntax tokens for <see cref="SqlGeneratorBase"/>.</summary>
public sealed class SqlServerDialect : SqlDialect
{
    /// <inheritdoc />
    public override DatabaseProvider Provider => DatabaseProvider.SqlServer;

    /// <inheritdoc />
    public override SqlDialectFeature Features =>
        SqlDialectFeature.Schemas
        | SqlDialectFeature.Sequences
        | SqlDialectFeature.CheckConstraints
        | SqlDialectFeature.GeneratedColumns;

    /// <inheritdoc />
    public override string QuoteIdentifier(string identifier) => $"[{identifier}]";

    /// <inheritdoc />
    public override string RenderIdentity(IdentityOptions identity) => identity.Strategy switch
    {
        IdentityStrategy.Identity or IdentityStrategy.AutoIncrement =>
            $" IDENTITY({identity.StartValue},{identity.Increment})",
        _ => string.Empty,
    };
}
