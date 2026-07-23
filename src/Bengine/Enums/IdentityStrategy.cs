namespace Bengine.Enums;

/// <summary>
/// Strategy used to auto-populate a column's value on insert.
/// </summary>
public enum IdentityStrategy
{
    /// <summary>No auto-population; the value is supplied by the caller.</summary>
    None,

    /// <summary>SQL-standard identity column (<c>GENERATED AS IDENTITY</c>).</summary>
    Identity,

    /// <summary>Value drawn from an owned or shared sequence object.</summary>
    Sequence,

    /// <summary>Provider-specific auto-increment (for example MySQL <c>AUTO_INCREMENT</c>).</summary>
    AutoIncrement,
}
