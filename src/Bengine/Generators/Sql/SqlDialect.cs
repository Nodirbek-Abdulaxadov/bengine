namespace Bengine.Generators.Sql;

using Bengine.Enums;
using Bengine.Models;
using Bengine.Models.Options;

/// <summary>
/// Encapsulates the provider-specific syntax the shared
/// <see cref="SqlGeneratorBase"/> composes: identifier quoting, feature
/// availability, identity rendering, and referential-action spelling. Concrete
/// dialects carry no structural logic, only tokens.
/// </summary>
public abstract class SqlDialect
{
    /// <summary>Gets the provider this dialect describes.</summary>
    public abstract DatabaseProvider Provider { get; }

    /// <summary>Gets the optional features supported by the provider.</summary>
    public abstract SqlDialectFeature Features { get; }

    /// <summary>Quotes an identifier using the provider's delimiter.</summary>
    /// <param name="identifier">The raw identifier.</param>
    /// <returns>The quoted identifier.</returns>
    public abstract string QuoteIdentifier(string identifier);

    /// <summary>
    /// Renders the inline identity / auto-increment clause for a column, or an
    /// empty string when the column is not identity-generated.
    /// </summary>
    /// <param name="identity">The column's identity options.</param>
    /// <returns>The identity clause including a leading space, or empty.</returns>
    public abstract string RenderIdentity(IdentityOptions identity);

    /// <summary>Gets a value indicating whether the provider supports the feature.</summary>
    /// <param name="feature">The feature to test.</param>
    /// <returns><c>true</c> if supported; otherwise <c>false</c>.</returns>
    public bool Supports(SqlDialectFeature feature) => (Features & feature) == feature;

    /// <summary>Renders a referential action keyword.</summary>
    /// <param name="action">The action to render.</param>
    /// <returns>The SQL keyword for the action.</returns>
    public virtual string RenderReferentialAction(ReferentialAction action) => action switch
    {
        ReferentialAction.NoAction => "NO ACTION",
        ReferentialAction.Restrict => "RESTRICT",
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => "NO ACTION",
    };

    /// <summary>Renders a default-value clause value, excluding the <c>DEFAULT</c> keyword.</summary>
    /// <param name="value">The default value to render.</param>
    /// <returns>The rendered default expression, or <c>null</c> when there is no default.</returns>
    public virtual string? RenderDefault(DefaultValue value) => value.Kind switch
    {
        DefaultValueKind.None => null,
        DefaultValueKind.Literal => value.Value,
        DefaultValueKind.Expression => value.Value,
        DefaultValueKind.Function => value.Value,
        DefaultValueKind.CurrentTimestamp => "CURRENT_TIMESTAMP",
        DefaultValueKind.CurrentDate => "CURRENT_DATE",
        DefaultValueKind.CurrentTime => "CURRENT_TIME",
        DefaultValueKind.Null => "NULL",
        _ => null,
    };
}
