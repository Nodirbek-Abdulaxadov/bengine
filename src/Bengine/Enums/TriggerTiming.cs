namespace Bengine.Enums;

/// <summary>
/// When a trigger fires relative to the triggering statement.
/// </summary>
public enum TriggerTiming
{
    /// <summary>Fire before the row operation is applied.</summary>
    Before,

    /// <summary>Fire after the row operation is applied.</summary>
    After,

    /// <summary>Fire instead of the operation (typically on views).</summary>
    InsteadOf,
}
