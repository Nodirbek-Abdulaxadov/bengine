namespace Bengine.Enums;

/// <summary>
/// Collation controlling string comparison and ordering for a textual column.
/// </summary>
public enum Collation
{
    /// <summary>Use the provider or database default collation.</summary>
    Default,

    /// <summary>Binary, byte-wise comparison.</summary>
    Binary,

    /// <summary>Case-sensitive comparison.</summary>
    CaseSensitive,

    /// <summary>Case-insensitive comparison.</summary>
    CaseInsensitive,

    /// <summary>Accent-insensitive comparison.</summary>
    AccentInsensitive,
}
