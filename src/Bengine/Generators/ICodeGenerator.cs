namespace Bengine.Generators;

using Bengine.Enums;
using Bengine.Models;

/// <summary>
/// Emits target-language source that models a schema. Implementations resolve
/// field types through <c>LanguageTypeMap</c> and never hardcode type names.
/// </summary>
public interface ICodeGenerator
{
    /// <summary>Gets the language this generator targets.</summary>
    Language Language { get; }

    /// <summary>Generates source modelling every table in a schema.</summary>
    /// <param name="schema">The schema to render.</param>
    /// <returns>The generated source.</returns>
    string Generate(DatabaseSchema schema);

    /// <summary>Generates the source modelling a single table.</summary>
    /// <param name="table">The table to render.</param>
    /// <returns>The generated source for one model.</returns>
    string GenerateModel(Table table);
}
