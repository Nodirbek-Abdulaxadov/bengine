namespace Bengine.Validation;

using Bengine.Enums;

/// <summary>
/// Immutable, single diagnostic produced during validation. The
/// <see cref="Code"/> is a stable identifier for programmatic handling; the
/// <see cref="Path"/> locates the offending element within the schema.
/// </summary>
public sealed record ValidationMessage
{
    /// <summary>Gets the severity of the diagnostic.</summary>
    public required ValidationSeverity Severity { get; init; }

    /// <summary>Gets a stable machine-readable code (for example <c>column.length.exceeded</c>).</summary>
    public required string Code { get; init; }

    /// <summary>Gets the human-readable description.</summary>
    public required string Message { get; init; }

    /// <summary>Gets a dotted path to the offending element (for example <c>orders.total</c>).</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Creates an error-severity message.</summary>
    /// <param name="code">The stable code.</param>
    /// <param name="message">The description.</param>
    /// <param name="path">The element path.</param>
    /// <returns>An error <see cref="ValidationMessage"/>.</returns>
    public static ValidationMessage Error(string code, string message, string path = "") =>
        new() { Severity = ValidationSeverity.Error, Code = code, Message = message, Path = path };

    /// <summary>Creates a warning-severity message.</summary>
    /// <param name="code">The stable code.</param>
    /// <param name="message">The description.</param>
    /// <param name="path">The element path.</param>
    /// <returns>A warning <see cref="ValidationMessage"/>.</returns>
    public static ValidationMessage Warning(string code, string message, string path = "") =>
        new() { Severity = ValidationSeverity.Warning, Code = code, Message = message, Path = path };
}
