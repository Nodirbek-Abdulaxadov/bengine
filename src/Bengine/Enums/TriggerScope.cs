namespace Bengine.Enums;

/// <summary>
/// Granularity at which a trigger's action executes.
/// </summary>
public enum TriggerScope
{
    /// <summary>Execute once for each affected row.</summary>
    Row,

    /// <summary>Execute once per triggering statement.</summary>
    Statement,
}
