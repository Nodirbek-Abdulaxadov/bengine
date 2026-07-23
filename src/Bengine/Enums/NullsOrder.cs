namespace Bengine.Enums;

/// <summary>
/// Placement of <c>NULL</c> values relative to non-null values in an ordered
/// index key.
/// </summary>
public enum NullsOrder
{
    /// <summary>Use the provider's default null ordering.</summary>
    Default,

    /// <summary>Sort <c>NULL</c> values before non-null values.</summary>
    First,

    /// <summary>Sort <c>NULL</c> values after non-null values.</summary>
    Last,
}
