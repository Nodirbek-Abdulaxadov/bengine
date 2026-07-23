namespace Bengine.Enums;

/// <summary>
/// Broad family a logical data type belongs to, used for grouping and for
/// selecting the applicable <see cref="ValidationKind"/>.
/// </summary>
public enum TypeCategory
{
    /// <summary>Boolean type.</summary>
    Boolean,

    /// <summary>Integer types.</summary>
    Integer,

    /// <summary>Exact and approximate numeric types.</summary>
    Numeric,

    /// <summary>Character and text types.</summary>
    Text,

    /// <summary>Binary and large-object types.</summary>
    Binary,

    /// <summary>Date, time, and timestamp types.</summary>
    Temporal,

    /// <summary>Universally unique identifier type.</summary>
    Uuid,

    /// <summary>Structured document types (JSON, XML).</summary>
    Document,
}
