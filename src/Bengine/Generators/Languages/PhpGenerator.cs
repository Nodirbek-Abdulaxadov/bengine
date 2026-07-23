namespace Bengine.Generators.Languages;

using System.Text;
using Bengine.Enums;
using Bengine.Extensions;
using Bengine.Models;

/// <summary>Generates PHP class models from a schema AST.</summary>
public sealed class PhpGenerator : CodeGeneratorBase
{
    /// <inheritdoc />
    public override Language Language => Language.Php;

    /// <inheritdoc />
    protected override string FileHeader(DatabaseSchema schema) => "<?php" + Environment.NewLine
        + Environment.NewLine + "declare(strict_types=1);";

    /// <inheritdoc />
    public override string GenerateModel(Table table)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"final class {table.Name.ToPascalCase()}");
        builder.AppendLine("{");

        foreach (var column in table.Columns)
        {
            var type = LanguageType(column);
            var nullable = column.IsNullable ? "?" : string.Empty;
            builder.AppendLine($"    public {nullable}{type} ${column.Name.ToCamelCase()};");
        }

        builder.Append('}');
        return builder.ToString();
    }
}
