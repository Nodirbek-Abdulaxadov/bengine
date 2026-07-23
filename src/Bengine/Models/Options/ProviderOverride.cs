namespace Bengine.Models.Options;

using System.Collections.Frozen;

/// <summary>
/// Immutable, provider-specific overrides for a single element. Escapes the
/// provider-independent model only where a concrete provider genuinely requires
/// a native type name or extra clause.
/// </summary>
public sealed record ProviderOverride
{
    /// <summary>Gets an explicit native type name that supersedes metadata mapping, if any.</summary>
    public string? NativeTypeName { get; init; }

    /// <summary>Gets additional provider-specific key/value settings.</summary>
    public FrozenDictionary<string, string> Settings { get; init; } =
        FrozenDictionary<string, string>.Empty;
}
