namespace Bengine.Validation;

using Bengine.Enums;

/// <summary>
/// Immutable aggregate of the diagnostics produced by a validation pass. A
/// result is valid when it contains no <see cref="ValidationSeverity.Error"/>
/// messages.
/// </summary>
public sealed record ValidationResult
{
    /// <summary>Gets the diagnostics collected during validation.</summary>
    public required IReadOnlyList<ValidationMessage> Messages { get; init; }

    /// <summary>Gets a value indicating whether no blocking errors were found.</summary>
    public bool IsValid => !Messages.Any(m => m.Severity == ValidationSeverity.Error);

    /// <summary>Gets the error-severity diagnostics.</summary>
    public IEnumerable<ValidationMessage> Errors =>
        Messages.Where(m => m.Severity == ValidationSeverity.Error);

    /// <summary>Gets a shared valid result carrying no diagnostics.</summary>
    public static ValidationResult Valid { get; } = new() { Messages = [] };

    /// <summary>Creates a result from a set of diagnostics.</summary>
    /// <param name="messages">The diagnostics.</param>
    /// <returns>A <see cref="ValidationResult"/> wrapping them.</returns>
    public static ValidationResult From(IEnumerable<ValidationMessage> messages) =>
        new() { Messages = [.. messages] };

    /// <summary>Merges several results into one.</summary>
    /// <param name="results">The results to combine.</param>
    /// <returns>A result containing every diagnostic from the inputs.</returns>
    public static ValidationResult Combine(IEnumerable<ValidationResult> results) =>
        new() { Messages = [.. results.SelectMany(r => r.Messages)] };
}
