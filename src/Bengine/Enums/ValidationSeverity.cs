namespace Bengine.Enums;

/// <summary>
/// Severity of a single validation message.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>An advisory issue that does not by itself invalidate the schema.</summary>
    Warning,

    /// <summary>A blocking issue that makes the schema invalid.</summary>
    Error,
}
