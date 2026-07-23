namespace Bengine.Validation.Rules;

using Bengine.Enums;
using Bengine.Metadata;
using Bengine.Models;

/// <summary>
/// Verifies that a column's declared scale is non-negative, within the metadata
/// maximum, and not greater than its precision.
/// </summary>
public sealed class ScaleRule : IColumnRule
{
    /// <inheritdoc />
    public IEnumerable<ValidationMessage> Evaluate(
        Column column, DbDataTypeMetadata metadata, DatabaseProvider provider, string path)
    {
        if (!metadata.Supports(TypeCapability.Scale) || column.Type.Scale is not { } scale)
        {
            yield break;
        }

        if (scale < 0)
        {
            yield return ValidationMessage.Error(
                "column.scale.negative",
                $"Scale {scale} must be non-negative for '{metadata.DataType}'.",
                path);
        }

        if (metadata.Limits.MaxScale is { } max && scale > max)
        {
            yield return ValidationMessage.Error(
                "column.scale.tooLarge",
                $"Scale {scale} exceeds the maximum {max} for '{metadata.DataType}'.",
                path);
        }

        if (column.Type.Precision is { } precision && scale > precision)
        {
            yield return ValidationMessage.Error(
                "column.scale.exceedsPrecision",
                $"Scale {scale} cannot exceed precision {precision} for '{metadata.DataType}'.",
                path);
        }
    }
}
