namespace Bengine.Generators.Languages;

using System.Text;
using Bengine.Enums;
using Bengine.Extensions;
using Bengine.Models;

/// <summary>Generates Go struct models from a schema AST.</summary>
public sealed class GoGenerator : CodeGeneratorBase
{
    /// <inheritdoc />
    public override Language Language => Language.Go;

    /// <inheritdoc />
    protected override string FileHeader(DatabaseSchema schema) => "package model";

    /// <inheritdoc />
    public override string GenerateModel(Table table)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"type {table.Name.ToPascalCase()} struct {{");

        foreach (var column in table.Columns)
        {
            var type = LanguageType(column);
            // Nullable columns are modelled as pointers so the zero value is distinguishable.
            var pointer = column.IsNullable ? "*" : string.Empty;
            var tag = $"`db:\"{column.Name}\"`";
            builder.AppendLine($"\t{column.Name.ToPascalCase()} {pointer}{type} {tag}");
        }

        builder.Append('}');
        return builder.ToString();
    }
}
