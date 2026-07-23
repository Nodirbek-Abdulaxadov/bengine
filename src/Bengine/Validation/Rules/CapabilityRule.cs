namespace Bengine.Validation.Rules;

using Bengine.Enums;
using Bengine.Metadata;
using Bengine.Models;

/// <summary>
/// Verifies that every structural modifier set on the column is meaningful for
/// its type according to the type's declared <see cref="TypeCapability"/> set.
/// </summary>
public sealed class CapabilityRule : IColumnRule
{
    /// <inheritdoc />
    public IEnumerable<ValidationMessage> Evaluate(
        Column column, DbDataTypeMetadata metadata, DatabaseProvider provider, string path)
    {
        var type = column.Type;

        if (type.Length is not null && !metadata.Supports(TypeCapability.Length))
        {
            yield return ValidationMessage.Error(
                "column.length.unsupported",
                $"Type '{metadata.DataType}' does not accept a length.",
                path);
        }

        if (type.Precision is not null && !metadata.Supports(TypeCapability.Precision))
        {
            yield return ValidationMessage.Error(
                "column.precision.unsupported",
                $"Type '{metadata.DataType}' does not accept a precision.",
                path);
        }

        if (type.Scale is not null && !metadata.Supports(TypeCapability.Scale))
        {
            yield return ValidationMessage.Error(
                "column.scale.unsupported",
                $"Type '{metadata.DataType}' does not accept a scale.",
                path);
        }

        if (type.IsUnsigned && !metadata.Supports(TypeCapability.Unsigned))
        {
            yield return ValidationMessage.Error(
                "column.unsigned.unsupported",
                $"Type '{metadata.DataType}' cannot be unsigned.",
                path);
        }

        if (column.Identity.Strategy != IdentityStrategy.None
            && !metadata.Supports(TypeCapability.AutoIncrement))
        {
            yield return ValidationMessage.Error(
                "column.identity.unsupported",
                $"Type '{metadata.DataType}' cannot participate in identity generation.",
                path);
        }
    }
}
