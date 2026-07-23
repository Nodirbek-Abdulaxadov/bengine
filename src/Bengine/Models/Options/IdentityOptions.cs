namespace Bengine.Models.Options;

using Bengine.Enums;

/// <summary>
/// Immutable configuration for an auto-populated column value. The
/// <see cref="Strategy"/> selects the mechanism; sequence-related fields apply
/// only when the strategy draws from a generator.
/// </summary>
public sealed record IdentityOptions
{
    /// <summary>Gets the generation strategy.</summary>
    public required IdentityStrategy Strategy { get; init; }

    /// <summary>Gets the name of the backing sequence, when the strategy uses one.</summary>
    public string? SequenceName { get; init; }

    /// <summary>Gets the first value produced by the generator.</summary>
    public long StartValue { get; init; } = 1;

    /// <summary>Gets the step between successive generated values.</summary>
    public long Increment { get; init; } = 1;

    /// <summary>Gets the minimum value the generator may produce, if bounded.</summary>
    public long? MinValue { get; init; }

    /// <summary>Gets the maximum value the generator may produce, if bounded.</summary>
    public long? MaxValue { get; init; }

    /// <summary>Gets the number of values pre-allocated for performance, if any.</summary>
    public int? Cache { get; init; }

    /// <summary>Gets a value indicating whether the generator wraps around after reaching its bound.</summary>
    public bool Cycle { get; init; }

    /// <summary>Gets an option representing the absence of auto-population.</summary>
    public static IdentityOptions None { get; } = new() { Strategy = IdentityStrategy.None };
}
