namespace Bengine.Enums;

/// <summary>
/// Describes whether a column accepts <c>NULL</c> values.
/// </summary>
public enum Nullability
{
    /// <summary>The column does not accept <c>NULL</c> values.</summary>
    NotNull,

    /// <summary>The column accepts <c>NULL</c> values.</summary>
    Nullable,
}
