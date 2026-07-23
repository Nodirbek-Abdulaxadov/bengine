namespace Bengine.Validation.Rules;

using Bengine.Enums;
using Bengine.Metadata;
using Bengine.Models;

/// <summary>
/// Verifies that a column's declared length falls within the inclusive bounds
/// published in the type's metadata limits.
/// </summary>
public sealed class LengthRule : IColumnRule
{
    /// <inheritdoc />
    public IEnumerable<ValidationMessage> Evaluate(
        Column column, DbDataTypeMetadata metadata, DatabaseProvider provider, string path)
    {
        if (!metadata.Supports(TypeCapability.Length) || column.Type.Length is not { } length)
        {
            yield break;
        }

        var limits = metadata.Limits;

        if (limits.MinLength is { } min && length < min)
        {
            yield return ValidationMessage.Error(
                "column.length.tooSmall",
                $"Length {length} is below the minimum {min} for '{metadata.DataType}'.",
                path);
        }

        if (limits.MaxLength is { } max && length > max)
        {
            yield return ValidationMessage.Error(
                "column.length.tooLarge",
                $"Length {length} exceeds the maximum {max} for '{metadata.DataType}'.",
                path);
        }
    }
}
