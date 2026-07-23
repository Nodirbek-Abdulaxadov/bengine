namespace Bengine.Generators.Languages;

using System.Text;
using Bengine.Enums;
using Bengine.Extensions;
using Bengine.Models;

/// <summary>Generates Rust struct models from a schema AST.</summary>
public sealed class RustGenerator : CodeGeneratorBase
{
    /// <inheritdoc />
    public override Language Language => Language.Rust;

    /// <inheritdoc />
    public override string GenerateModel(Table table)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#[derive(Debug, Clone)]");
        builder.AppendLine($"pub struct {table.Name.ToPascalCase()} {{");

        foreach (var column in table.Columns)
        {
            var type = LanguageType(column);
            var rendered = column.IsNullable ? $"Option<{type}>" : type;
            builder.AppendLine($"    pub {column.Name.ToSnakeCase()}: {rendered},");
        }

        builder.Append('}');
        return builder.ToString();
    }
}
