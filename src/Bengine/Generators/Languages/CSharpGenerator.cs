namespace Bengine.Generators.Languages;

using System.Text;
using Bengine.Enums;
using Bengine.Extensions;
using Bengine.Models;

/// <summary>Generates C# record-style models from a schema AST.</summary>
public sealed class CSharpGenerator : CodeGeneratorBase
{
    /// <inheritdoc />
    public override Language Language => Language.CSharp;

    /// <inheritdoc />
    protected override string FileHeader(DatabaseSchema schema) => "#nullable enable";

    /// <inheritdoc />
    public override string GenerateModel(Table table)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"public sealed class {table.Name.ToPascalCase()}");
        builder.AppendLine("{");

        foreach (var column in table.Columns)
        {
            var type = LanguageType(column);
            var nullable = column.IsNullable ? "?" : string.Empty;
            builder.AppendLine(
                $"    public {type}{nullable} {column.Name.ToPascalCase()} {{ get; set; }}");
        }

        builder.Append('}');
        return builder.ToString();
    }
}
