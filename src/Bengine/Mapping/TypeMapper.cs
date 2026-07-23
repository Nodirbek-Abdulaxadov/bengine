namespace Bengine.Mapping;

using System.Globalization;
using Bengine.Enums;
using Bengine.Metadata;
using Bengine.Models.Options;

/// <summary>
/// Default <see cref="ITypeMapper"/>. Resolves the base native spelling through
/// <see cref="ProviderTypeMap"/> / <see cref="LanguageTypeMap"/> and appends
/// structural modifiers only where <see cref="DbDataTypeRegistry"/> reports the
/// type supports them, so no type facts are hardcoded here.
/// </summary>
public sealed class TypeMapper : ITypeMapper
{
    /// <summary>Gets a shared, stateless instance.</summary>
    public static TypeMapper Instance { get; } = new();

    /// <inheritdoc />
    public string RenderProviderType(DatabaseProvider provider, ColumnTypeOptions type)
    {
        var baseName = ProviderTypeMap.ToNative(provider, type.DataType);

        // A native spelling that already carries its own parentheses (for
        // example varchar(max) or tinyint(1)) is fixed and takes no modifiers.
        if (baseName.Contains('(', StringComparison.Ordinal))
        {
            return baseName;
        }

        var metadata = DbDataTypeRegistry.Get(type.DataType);
        return baseName + RenderModifier(metadata, type);
    }

    /// <inheritdoc />
    public string RenderLanguageType(Language language, ColumnTypeOptions type) =>
        LanguageTypeMap.ToNative(language, type.DataType);

    private static string RenderModifier(DbDataTypeMetadata metadata, ColumnTypeOptions type)
    {
        if (metadata.Supports(TypeCapability.Precision))
        {
            var precision = type.Precision ?? metadata.Limits.DefaultPrecision;
            if (precision is null)
            {
                return string.Empty;
            }

            if (metadata.Supports(TypeCapability.Scale))
            {
                var scale = type.Scale ?? metadata.Limits.DefaultScale ?? 0;
                return string.Create(
                    CultureInfo.InvariantCulture, $"({precision.Value},{scale})");
            }

            return string.Create(CultureInfo.InvariantCulture, $"({precision.Value})");
        }

        if (metadata.Supports(TypeCapability.Length))
        {
            var length = type.Length ?? metadata.Limits.DefaultLength;
            return length is null
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $"({length.Value})");
        }

        return string.Empty;
    }
}
