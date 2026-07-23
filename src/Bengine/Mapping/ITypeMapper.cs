namespace Bengine.Mapping;

using Bengine.Enums;
using Bengine.Models.Options;

/// <summary>
/// Renders a column's logical type into a fully-specified native declaration for
/// a provider or language, folding in length, precision, and scale drawn from
/// metadata. Implementations are provider-independent and metadata-driven.
/// </summary>
public interface ITypeMapper
{
    /// <summary>Renders the complete native type declaration for a provider.</summary>
    /// <param name="provider">The target provider.</param>
    /// <param name="type">The column type options to render.</param>
    /// <returns>A native type declaration such as <c>varchar(255)</c> or <c>numeric(18,2)</c>.</returns>
    string RenderProviderType(DatabaseProvider provider, ColumnTypeOptions type);

    /// <summary>Renders the idiomatic language type for the column's logical type.</summary>
    /// <param name="language">The target language.</param>
    /// <param name="type">The column type options to render.</param>
    /// <returns>An idiomatic language type name.</returns>
    string RenderLanguageType(Language language, ColumnTypeOptions type);
}
