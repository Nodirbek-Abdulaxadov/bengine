namespace Bengine.Models.Options;

using System.Collections.Frozen;
using Bengine.Enums;

/// <summary>
/// Immutable collection of per-provider overrides keyed by
/// <see cref="DatabaseProvider"/>. Absent providers fall back entirely to the
/// provider-independent model and metadata.
/// </summary>
public sealed record ProviderOptions
{
    /// <summary>Gets the overrides keyed by target provider.</summary>
    public FrozenDictionary<DatabaseProvider, ProviderOverride> Overrides { get; init; } =
        FrozenDictionary<DatabaseProvider, ProviderOverride>.Empty;

    /// <summary>Gets an instance carrying no overrides.</summary>
    public static ProviderOptions Empty { get; } = new();

    /// <summary>Attempts to resolve the override for a provider.</summary>
    /// <param name="provider">The provider whose override is requested.</param>
    /// <param name="value">The resolved override when present.</param>
    /// <returns><c>true</c> if an override exists for <paramref name="provider"/>; otherwise <c>false</c>.</returns>
    public bool TryGet(DatabaseProvider provider, out ProviderOverride? value) =>
        Overrides.TryGetValue(provider, out value);
}
