namespace Bengine.Enums;

/// <summary>
/// Action performed on a referencing row when the referenced row is deleted or
/// updated, as declared by a foreign key's <c>ON DELETE</c> / <c>ON UPDATE</c>
/// clause.
/// </summary>
public enum ReferentialAction
{
    /// <summary>Reject the operation if dependent rows exist (deferred check).</summary>
    NoAction,

    /// <summary>Reject the operation immediately if dependent rows exist.</summary>
    Restrict,

    /// <summary>Propagate the delete or update to the referencing rows.</summary>
    Cascade,

    /// <summary>Set the referencing columns to <c>NULL</c>.</summary>
    SetNull,

    /// <summary>Set the referencing columns to their declared default value.</summary>
    SetDefault,
}
