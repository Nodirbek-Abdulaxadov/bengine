namespace Bengine.Enums;

/// <summary>
/// Classifies the family of value constraints a logical data type can carry.
/// </summary>
public enum ValidationKind
{
    /// <summary>No value validation applies.</summary>
    None,

    /// <summary>Numeric range, precision, and scale validation.</summary>
    Numeric,

    /// <summary>String length and pattern validation.</summary>
    String,

    /// <summary>Date and time range validation.</summary>
    Date,

    /// <summary>Binary length validation.</summary>
    Binary,
}
