namespace Bengine.Enums;

/// <summary>
/// Classifies the form of a column's default value so generators can emit the
/// correct SQL without inspecting raw text.
/// </summary>
public enum DefaultValueKind
{
    /// <summary>No default value is defined.</summary>
    None,

    /// <summary>A literal constant (number, quoted string, boolean).</summary>
    Literal,

    /// <summary>A provider-evaluated SQL expression.</summary>
    Expression,

    /// <summary>A function invocation (for example <c>gen_random_uuid()</c>).</summary>
    Function,

    /// <summary>The current transaction timestamp.</summary>
    CurrentTimestamp,

    /// <summary>The current date.</summary>
    CurrentDate,

    /// <summary>The current time.</summary>
    CurrentTime,

    /// <summary>An explicit <c>NULL</c> default.</summary>
    Null,
}
