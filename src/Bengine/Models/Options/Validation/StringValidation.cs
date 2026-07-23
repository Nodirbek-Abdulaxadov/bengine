namespace Bengine.Models.Options.Validation;

/// <summary>
/// Immutable value constraints applied to a textual column, independent of any
/// provider.
/// </summary>
public sealed record StringValidation
{
    /// <summary>Gets the inclusive minimum permitted length in characters, if constrained.</summary>
    public int? MinLength { get; init; }

    /// <summary>Gets the inclusive maximum permitted length in characters, if constrained.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Gets a regular-expression pattern the value must match, if any.</summary>
    public string? Pattern { get; init; }

    /// <summary>Gets a value indicating whether empty strings are permitted.</summary>
    public bool AllowEmpty { get; init; } = true;
}
