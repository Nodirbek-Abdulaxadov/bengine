namespace Bengine.Models.Options.Validation;

using Bengine.Enums;

/// <summary>
/// Aggregates the value-level validation attached to a single column. Exactly
/// one family is populated according to <see cref="Kind"/>; the others remain
/// <c>null</c>.
/// </summary>
public sealed record ColumnValidation
{
    /// <summary>Gets the family of validation this instance carries.</summary>
    public required ValidationKind Kind { get; init; }

    /// <summary>Gets the numeric constraints, when <see cref="Kind"/> is <see cref="ValidationKind.Numeric"/>.</summary>
    public NumericValidation? Numeric { get; init; }

    /// <summary>Gets the string constraints, when <see cref="Kind"/> is <see cref="ValidationKind.String"/>.</summary>
    public StringValidation? String { get; init; }

    /// <summary>Gets the temporal constraints, when <see cref="Kind"/> is <see cref="ValidationKind.Date"/>.</summary>
    public DateValidation? Date { get; init; }

    /// <summary>Gets an empty validation carrying no constraints.</summary>
    public static ColumnValidation None { get; } = new() { Kind = ValidationKind.None };
}
