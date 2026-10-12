namespace Bengine.Introspection.Model;

/// <summary>
/// A comparison a <see cref="FilterCondition"/> applies to a column when
/// browsing table data. The reader renders each operator as parameterized SQL,
/// never by interpolating the value, so filters cannot inject SQL.
/// </summary>
public enum FilterOperator
{
    /// <summary><c>column = value</c>.</summary>
    Equals,

    /// <summary><c>column &lt;&gt; value</c>.</summary>
    NotEquals,

    /// <summary><c>column &lt; value</c>.</summary>
    LessThan,

    /// <summary><c>column &lt;= value</c>.</summary>
    LessThanOrEqual,

    /// <summary><c>column &gt; value</c>.</summary>
    GreaterThan,

    /// <summary><c>column &gt;= value</c>.</summary>
    GreaterThanOrEqual,

    /// <summary>The column, rendered as text, contains the value (case-insensitive).</summary>
    Contains,

    /// <summary>The column, rendered as text, starts with the value (case-insensitive).</summary>
    StartsWith,

    /// <summary>The column, rendered as text, ends with the value (case-insensitive).</summary>
    EndsWith,

    /// <summary><c>column IN (values...)</c>.</summary>
    In,

    /// <summary><c>column IS NULL</c>; the condition's value is ignored.</summary>
    IsNull,

    /// <summary><c>column IS NOT NULL</c>; the condition's value is ignored.</summary>
    IsNotNull,
}
