namespace Bengine.Validation.Rules;

using Bengine.Enums;
using Bengine.Metadata;
using Bengine.Models;

/// <summary>
/// Verifies that the target provider natively supports the column's logical
/// type, using metadata as the sole authority.
/// </summary>
public sealed class ProviderSupportRule : IColumnRule
{
    /// <inheritdoc />
    public IEnumerable<ValidationMessage> Evaluate(
        Column column, DbDataTypeMetadata metadata, DatabaseProvider provider, string path)
    {
        if (!metadata.SupportedBy(provider))
        {
            yield return ValidationMessage.Error(
                "column.type.unsupported",
                $"Type '{metadata.DataType}' is not supported by provider '{provider}'.",
                path);
        }
    }
}
