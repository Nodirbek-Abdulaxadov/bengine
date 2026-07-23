namespace Bengine.Enums;

/// <summary>
/// Optional SQL features whose availability differs between providers. Used by
/// generators and validators to branch without hardcoding provider checks.
/// </summary>
[System.Flags]
public enum SqlDialectFeature
{
    /// <summary>No optional features.</summary>
    None = 0,

    /// <summary>Supports <c>IF NOT EXISTS</c> on create statements.</summary>
    IfNotExists = 1 << 0,

    /// <summary>Supports schema-qualified object names.</summary>
    Schemas = 1 << 1,

    /// <summary>Supports sequence objects.</summary>
    Sequences = 1 << 2,

    /// <summary>Supports check constraints.</summary>
    CheckConstraints = 1 << 3,

    /// <summary>Supports partial (filtered) indexes.</summary>
    PartialIndexes = 1 << 4,

    /// <summary>Supports deferrable constraints.</summary>
    DeferrableConstraints = 1 << 5,

    /// <summary>Supports generated / computed columns.</summary>
    GeneratedColumns = 1 << 6,
}
