namespace Bengine.Models.Options.Validation;

/// <summary>
/// Immutable value constraints applied to a temporal column, independent of any
/// provider.
/// </summary>
public sealed record DateValidation
{
    /// <summary>Gets the inclusive earliest permitted value, if constrained.</summary>
    public DateTimeOffset? MinValue { get; init; }

    /// <summary>Gets the inclusive latest permitted value, if constrained.</summary>
    public DateTimeOffset? MaxValue { get; init; }

    /// <summary>Gets a value indicating whether values in the future are permitted.</summary>
    public bool AllowFuture { get; init; } = true;

    /// <summary>Gets a value indicating whether values in the past are permitted.</summary>
    public bool AllowPast { get; init; } = true;
}
