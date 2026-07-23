namespace Bengine.Metadata;

/// <summary>
/// Immutable size and range limits for a logical data type. All members are
/// nullable so a limit can be marked "not applicable" for a given type rather
/// than defaulting to a misleading zero.
/// </summary>
public sealed record DbTypeLimits
{
    /// <summary>Gets the length applied when none is specified, if the type is length-bearing.</summary>
    public int? DefaultLength { get; init; }

    /// <summary>Gets the smallest permitted length, if the type is length-bearing.</summary>
    public int? MinLength { get; init; }

    /// <summary>Gets the largest permitted length, if the type is length-bearing.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Gets the smallest representable value, if the type is numeric.</summary>
    public decimal? MinValue { get; init; }

    /// <summary>Gets the largest representable value, if the type is numeric.</summary>
    public decimal? MaxValue { get; init; }

    /// <summary>Gets the precision applied when none is specified, if the type supports precision.</summary>
    public int? DefaultPrecision { get; init; }

    /// <summary>Gets the largest permitted precision, if the type supports precision.</summary>
    public int? MaxPrecision { get; init; }

    /// <summary>Gets the scale applied when none is specified, if the type supports scale.</summary>
    public int? DefaultScale { get; init; }

    /// <summary>Gets the largest permitted scale, if the type supports scale.</summary>
    public int? MaxScale { get; init; }

    /// <summary>Gets a shared instance carrying no limits.</summary>
    public static DbTypeLimits None { get; } = new();
}
