namespace Bengine.Enums;

/// <summary>
/// Describes how a computed (generated) column's value is materialized.
/// </summary>
public enum GeneratedKind
{
    /// <summary>The column is not generated.</summary>
    None,

    /// <summary>The value is computed and physically stored on write.</summary>
    Stored,

    /// <summary>The value is computed on read and not stored.</summary>
    Virtual,
}
