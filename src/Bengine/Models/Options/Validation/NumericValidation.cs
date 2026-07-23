namespace Bengine.Models.Options.Validation;

/// <summary>
/// Immutable value constraints applied to a numeric column, independent of any
/// provider. Bounds are expressed as <see cref="decimal"/> to preserve exact
/// values across integer and fixed-point types.
/// </summary>
public sealed record NumericValidation
{
    /// <summary>Gets the inclusive minimum permitted value, if constrained.</summary>
    public decimal? MinValue { get; init; }

    /// <summary>Gets the inclusive maximum permitted value, if constrained.</summary>
    public decimal? MaxValue { get; init; }

    /// <summary>Gets the required total number of significant digits, if constrained.</summary>
    public int? Precision { get; init; }

    /// <summary>Gets the required number of fractional digits, if constrained.</summary>
    public int? Scale { get; init; }

    /// <summary>Gets a value indicating whether negative values are permitted.</summary>
    public bool AllowNegative { get; init; } = true;
}
