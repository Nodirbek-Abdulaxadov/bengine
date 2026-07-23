namespace Bengine.Generators.Sql;

using Bengine.Enums;
using Bengine.Models.Options;

/// <summary>MySQL syntax tokens for <see cref="SqlGeneratorBase"/>.</summary>
public sealed class MySqlDialect : SqlDialect
{
    /// <inheritdoc />
    public override DatabaseProvider Provider => DatabaseProvider.MySql;

    /// <inheritdoc />
    public override SqlDialectFeature Features =>
        SqlDialectFeature.IfNotExists
        | SqlDialectFeature.CheckConstraints
        | SqlDialectFeature.GeneratedColumns;

    /// <inheritdoc />
    public override string QuoteIdentifier(string identifier) => $"`{identifier}`";

    /// <inheritdoc />
    public override string RenderIdentity(IdentityOptions identity) => identity.Strategy switch
    {
        IdentityStrategy.Identity or IdentityStrategy.AutoIncrement => " AUTO_INCREMENT",
        _ => string.Empty,
    };
}
