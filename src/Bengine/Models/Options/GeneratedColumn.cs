namespace Bengine.Models.Options;

using Bengine.Enums;

/// <summary>
/// Immutable definition of a computed (generated) column's value. The
/// <see cref="Expression"/> is a provider-evaluated formula; whether it is
/// materialized is controlled by <see cref="Kind"/>.
/// </summary>
public sealed record GeneratedColumn
{
    /// <summary>Gets how the generated value is materialized.</summary>
    public required GeneratedKind Kind { get; init; }

    /// <summary>Gets the expression used to compute the value.</summary>
    public required string Expression { get; init; }

    /// <summary>Creates a stored generated column.</summary>
    /// <param name="expression">The computation expression.</param>
    /// <returns>A stored <see cref="GeneratedColumn"/>.</returns>
    public static GeneratedColumn Stored(string expression) =>
        new() { Kind = GeneratedKind.Stored, Expression = expression };

    /// <summary>Creates a virtual generated column.</summary>
    /// <param name="expression">The computation expression.</param>
    /// <returns>A virtual <see cref="GeneratedColumn"/>.</returns>
    public static GeneratedColumn Virtual(string expression) =>
        new() { Kind = GeneratedKind.Virtual, Expression = expression };
}
