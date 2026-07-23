namespace Bengine.Generators.Languages;

using Bengine.Enums;
using Bengine.Mapping;
using Bengine.Models;

/// <summary>
/// Shared scaffolding for language code generators. Concrete generators supply
/// only the language-specific rendering of a model; type resolution always flows
/// through <see cref="LanguageTypeMap"/> so no language types are hardcoded.
/// </summary>
public abstract class CodeGeneratorBase : ICodeGenerator
{
    /// <inheritdoc />
    public abstract Language Language { get; }

    /// <inheritdoc />
    public virtual string Generate(DatabaseSchema schema)
    {
        var header = FileHeader(schema);
        var models = string.Join(
            Environment.NewLine + Environment.NewLine,
            schema.Tables.Select(GenerateModel));

        return (header.Length == 0 ? string.Empty : header + Environment.NewLine + Environment.NewLine)
               + models + Environment.NewLine;
    }

    /// <inheritdoc />
    public abstract string GenerateModel(Table table);

    /// <summary>Renders an optional file-level header (imports, package, pragmas).</summary>
    /// <param name="schema">The schema being generated.</param>
    /// <returns>The header text, or an empty string when none is needed.</returns>
    protected virtual string FileHeader(DatabaseSchema schema) => string.Empty;

    /// <summary>Resolves the idiomatic language type for a column.</summary>
    /// <param name="column">The column whose type to resolve.</param>
    /// <returns>The language type name.</returns>
    protected string LanguageType(Column column) =>
        LanguageTypeMap.ToNative(Language, column.Type.DataType);
}
