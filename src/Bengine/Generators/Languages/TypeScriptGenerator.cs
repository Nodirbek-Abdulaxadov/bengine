namespace Bengine.Generators.Languages;

using System.Text;
using Bengine.Enums;
using Bengine.Extensions;
using Bengine.Models;

/// <summary>Generates TypeScript interface models from a schema AST.</summary>
public sealed class TypeScriptGenerator : CodeGeneratorBase
{
    /// <inheritdoc />
    public override Language Language => Language.TypeScript;

    /// <inheritdoc />
    public override string GenerateModel(Table table)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"export interface {table.Name.ToPascalCase()} {{");

        foreach (var column in table.Columns)
        {
            var type = LanguageType(column);
            var optional = column.IsNullable ? "?" : string.Empty;
            var rendered = column.IsNullable ? $"{type} | null" : type;
            builder.AppendLine($"  {column.Name.ToCamelCase()}{optional}: {rendered};");
        }

        builder.Append('}');
        return builder.ToString();
    }
}
