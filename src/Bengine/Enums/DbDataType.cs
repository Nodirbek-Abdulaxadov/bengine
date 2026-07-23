namespace Bengine.Enums;

/// <summary>
/// Canonical, provider-independent logical data types understood by the schema
/// framework. Concrete provider or language spellings are resolved through the
/// mapping and metadata layers; this enum is never a raw SQL type name.
/// </summary>
public enum DbDataType
{
    /// <summary>Boolean value.</summary>
    Boolean,

    /// <summary>8-bit signed integer.</summary>
    TinyInt,

    /// <summary>16-bit signed integer.</summary>
    SmallInt,

    /// <summary>32-bit signed integer.</summary>
    Integer,

    /// <summary>64-bit signed integer.</summary>
    BigInt,

    /// <summary>Exact numeric with configurable precision and scale.</summary>
    Decimal,

    /// <summary>32-bit IEEE floating point.</summary>
    Real,

    /// <summary>64-bit IEEE floating point.</summary>
    Double,

    /// <summary>Fixed-length character string.</summary>
    Char,

    /// <summary>Variable-length character string with a bounded length.</summary>
    VarChar,

    /// <summary>Unbounded variable-length character string.</summary>
    Text,

    /// <summary>Fixed-length binary data.</summary>
    Binary,

    /// <summary>Variable-length binary data with a bounded length.</summary>
    VarBinary,

    /// <summary>Unbounded variable-length binary data.</summary>
    Blob,

    /// <summary>Calendar date without a time component.</summary>
    Date,

    /// <summary>Time of day without a date component.</summary>
    Time,

    /// <summary>Date and time without time-zone information.</summary>
    Timestamp,

    /// <summary>Date and time with time-zone information.</summary>
    TimestampTz,

    /// <summary>Universally unique identifier.</summary>
    Uuid,

    /// <summary>JSON document.</summary>
    Json,

    /// <summary>Binary JSON document.</summary>
    JsonBinary,

    /// <summary>XML document.</summary>
    Xml,
}
