namespace Bengine.Validation.Rules;

using Bengine.Enums;
using Bengine.Metadata;
using Bengine.Models;

/// <summary>
/// A single, focused validation rule applied to a column in the context of a
/// provider and the column's resolved type metadata. Keeping each rule
/// independent honors the single-responsibility principle and lets the column
/// validator compose them freely.
/// </summary>
public interface IColumnRule
{
    /// <summary>Applies the rule to a column.</summary>
    /// <param name="column">The column under validation.</param>
    /// <param name="metadata">The resolved metadata for the column's type.</param>
    /// <param name="provider">The target provider.</param>
    /// <param name="path">The dotted path locating the column.</param>
    /// <returns>The diagnostics produced by this rule; empty when the rule passes.</returns>
    IEnumerable<ValidationMessage> Evaluate(
        Column column, DbDataTypeMetadata metadata, DatabaseProvider provider, string path);
}
