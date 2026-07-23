namespace Bengine.Generators.Languages;

using System.Text;
using Bengine.Enums;
using Bengine.Extensions;
using Bengine.Models;

/// <summary>Generates Python dataclass models from a schema AST.</summary>
public sealed class PythonGenerator : CodeGeneratorBase
{
    /// <inheritdoc />
    public override Language Language => Language.Python;

    /// <inheritdoc />
    protected override string FileHeader(DatabaseSchema schema) =>
        "from __future__ import annotations" + Environment.NewLine
        + "import datetime" + Environment.NewLine
        + "import decimal" + Environment.NewLine
        + "import uuid" + Environment.NewLine
        + "from dataclasses import dataclass" + Environment.NewLine
        + "from typing import Optional";

    /// <inheritdoc />
    public override string GenerateModel(Table table)
    {
        var builder = new StringBuilder();
        builder.AppendLine("@dataclass");
        builder.AppendLine($"class {table.Name.ToPascalCase()}:");

        if (table.Columns.Count == 0)
        {
            builder.Append("    pass");
            return builder.ToString();
        }

        foreach (var column in table.Columns)
        {
            var type = LanguageType(column);
            var rendered = column.IsNullable ? $"Optional[{type}]" : type;
            builder.AppendLine($"    {column.Name.ToSnakeCase()}: {rendered}");
        }

        return builder.ToString().TrimEnd();
    }
}
