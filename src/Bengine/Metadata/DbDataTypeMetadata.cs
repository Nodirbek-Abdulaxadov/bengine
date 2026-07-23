namespace Bengine.Metadata;

using System.Collections.Frozen;
using Bengine.Enums;

/// <summary>
/// Immutable, authoritative description of a single logical data type. Metadata
/// is the single source of truth: validators, mappers, and generators consult
/// this record instead of embedding hardcoded knowledge about types.
/// </summary>
public sealed record DbDataTypeMetadata
{
    /// <summary>Gets the logical type this metadata describes.</summary>
    public required DbDataType DataType { get; init; }

    /// <summary>Gets the broad family the type belongs to.</summary>
    public required TypeCategory Category { get; init; }

    /// <summary>Gets the structural capabilities the type exposes.</summary>
    public required TypeCapability Capabilities { get; init; }

    /// <summary>Gets the value-validation family that applies to the type.</summary>
    public required ValidationKind ValidationKind { get; init; }

    /// <summary>Gets the providers that natively support the type.</summary>
    public required FrozenSet<DatabaseProvider> SupportedProviders { get; init; }

    /// <summary>Gets the target languages that can represent the type.</summary>
    public required FrozenSet<Language> SupportedLanguages { get; init; }

    /// <summary>Gets the validation families that may be attached to the type.</summary>
    public required FrozenSet<ValidationKind> SupportedValidations { get; init; }

    /// <summary>Gets the size and range limits for the type.</summary>
    public required DbTypeLimits Limits { get; init; }

    /// <summary>Determines whether the type declares the supplied capability.</summary>
    /// <param name="capability">The capability to test.</param>
    /// <returns><c>true</c> if the capability is present; otherwise <c>false</c>.</returns>
    public bool Supports(TypeCapability capability) => (Capabilities & capability) == capability;

    /// <summary>Determines whether the supplied provider natively supports the type.</summary>
    /// <param name="provider">The provider to test.</param>
    /// <returns><c>true</c> if supported; otherwise <c>false</c>.</returns>
    public bool SupportedBy(DatabaseProvider provider) => SupportedProviders.Contains(provider);

    /// <summary>Determines whether the supplied language can represent the type.</summary>
    /// <param name="language">The language to test.</param>
    /// <returns><c>true</c> if representable; otherwise <c>false</c>.</returns>
    public bool RepresentableIn(Language language) => SupportedLanguages.Contains(language);
}
