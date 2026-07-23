namespace Bengine.Enums;

/// <summary>
/// Data-modification events that can activate a trigger. Values are combinable.
/// </summary>
[System.Flags]
public enum TriggerEvent
{
    /// <summary>No event.</summary>
    None = 0,

    /// <summary>Row insertion.</summary>
    Insert = 1 << 0,

    /// <summary>Row update.</summary>
    Update = 1 << 1,

    /// <summary>Row deletion.</summary>
    Delete = 1 << 2,

    /// <summary>Table truncation.</summary>
    Truncate = 1 << 3,
}
