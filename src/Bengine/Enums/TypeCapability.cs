namespace Bengine.Enums;

/// <summary>
/// Bit flags describing the structural knobs a logical data type exposes. Used
/// by metadata and validation to decide which options are meaningful for a type.
/// </summary>
[System.Flags]
public enum TypeCapability
{
    /// <summary>No capabilities.</summary>
    None = 0,

    /// <summary>Supports a bounded length (for example <c>VARCHAR(n)</c>).</summary>
    Length = 1 << 0,

    /// <summary>Supports numeric precision.</summary>
    Precision = 1 << 1,

    /// <summary>Supports numeric scale.</summary>
    Scale = 1 << 2,

    /// <summary>May be marked unsigned.</summary>
    Unsigned = 1 << 3,

    /// <summary>May participate in identity / auto-increment generation.</summary>
    AutoIncrement = 1 << 4,

    /// <summary>Supports a fractional-seconds precision for temporal values.</summary>
    FractionalSeconds = 1 << 5,

    /// <summary>Supports a character set / collation.</summary>
    Collatable = 1 << 6,

    /// <summary>Represents large object storage.</summary>
    LargeObject = 1 << 7,
}
