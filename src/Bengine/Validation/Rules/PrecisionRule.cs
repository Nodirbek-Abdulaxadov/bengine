namespace Bengine.Validation.Rules;

using Bengine.Enums;
using Bengine.Metadata;
using Bengine.Models;

/// <summary>
/// Verifies that a column's declared precision does not exceed the maximum
/// published in the type's metadata limits.
/// </summary>
public sealed class PrecisionRule : IColumnRule
{
    /// <inheritdoc />
    public IEnumerable<ValidationMessage> Evaluate(
        Column column, DbDataTypeMetadata metadata, DatabaseProvider provider, string path)
    {
        if (!metadata.Supports(TypeCapability.Precision) || column.Type.Precision is not { } precision)
        {
            yield break;
        }

        if (precision < 1)
        {
            yield return ValidationMessage.Error(
                "column.precision.tooSmall",
                $"Precision {precision} must be at least 1 for '{metadata.DataType}'.",
                path);
        }

        if (metadata.Limits.MaxPrecision is { } max && precision > max)
        {
            yield return ValidationMessage.Error(
                "column.precision.tooLarge",
                $"Precision {precision} exceeds the maximum {max} for '{metadata.DataType}'.",
                path);
        }
    }
}
