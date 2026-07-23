namespace Bengine.Validation;

using Bengine.Enums;

/// <summary>
/// Validates a schema element against a specific provider's rules. Validators
/// draw all type facts from metadata rather than hardcoding provider behavior.
/// </summary>
/// <typeparam name="T">The element type being validated.</typeparam>
public interface IValidator<in T>
{
    /// <summary>Validates the element for the target provider.</summary>
    /// <param name="target">The element to validate.</param>
    /// <param name="provider">The provider the schema will be generated for.</param>
    /// <returns>The aggregated validation result.</returns>
    ValidationResult Validate(T target, DatabaseProvider provider);
}
