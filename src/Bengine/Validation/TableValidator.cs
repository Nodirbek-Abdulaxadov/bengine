namespace Bengine.Validation;

using Bengine.Enums;
using Bengine.Models;

/// <summary>
/// Validates a table: every column is validated individually, and the table's
/// owned constraints (primary key, foreign keys, indexes) are checked for
/// consistent references to declared columns.
/// </summary>
public sealed class TableValidator : IValidator<Table>
{
    private readonly ColumnValidator _columnValidator;

    /// <summary>Initializes a validator with the default column validator.</summary>
    public TableValidator()
        : this(new ColumnValidator())
    {
    }

    /// <summary>Initializes a validator with an explicit column validator.</summary>
    /// <param name="columnValidator">The column validator to delegate to.</param>
    public TableValidator(ColumnValidator columnValidator) => _columnValidator = columnValidator;

    /// <inheritdoc />
    public ValidationResult Validate(Table target, DatabaseProvider provider)
    {
        var messages = new List<ValidationMessage>();
        var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in target.Columns)
        {
            var path = $"{target.Name}.{column.Name}";

            if (!columnNames.Add(column.Name))
            {
                messages.Add(ValidationMessage.Error(
                    "table.column.duplicate",
                    $"Duplicate column name '{column.Name}'.",
                    path));
            }

            messages.AddRange(_columnValidator.Validate(column, provider, path).Messages);
        }

        if (target.Columns.Count == 0)
        {
            messages.Add(ValidationMessage.Error(
                "table.columns.empty", "A table must declare at least one column.", target.Name));
        }

        ValidateKeyReferences(target, columnNames, messages);
        return ValidationResult.From(messages);
    }

    private static void ValidateKeyReferences(
        Table table, HashSet<string> columnNames, List<ValidationMessage> messages)
    {
        if (table.PrimaryKey is { } pk)
        {
            foreach (var name in pk.ColumnNames.Where(name => !columnNames.Contains(name)))
            {
                messages.Add(ValidationMessage.Error(
                    "table.primaryKey.unknownColumn",
                    $"Primary key references unknown column '{name}'.",
                    $"{table.Name}.{name}"));
            }
        }

        foreach (var fk in table.ForeignKeys)
        {
            foreach (var name in fk.Columns.Where(name => !columnNames.Contains(name)))
            {
                messages.Add(ValidationMessage.Error(
                    "table.foreignKey.unknownColumn",
                    $"Foreign key '{fk.Name}' references unknown local column '{name}'.",
                    $"{table.Name}.{fk.Name}"));
            }

            if (fk.Columns.Count != fk.ReferencedColumns.Count)
            {
                messages.Add(ValidationMessage.Error(
                    "table.foreignKey.arity",
                    $"Foreign key '{fk.Name}' local and referenced column counts differ.",
                    $"{table.Name}.{fk.Name}"));
            }
        }

        foreach (var index in table.Indexes)
        {
            foreach (var ic in index.Columns.Where(ic => !columnNames.Contains(ic.ColumnName)))
            {
                messages.Add(ValidationMessage.Error(
                    "table.index.unknownColumn",
                    $"Index '{index.Name}' references unknown column '{ic.ColumnName}'.",
                    $"{table.Name}.{index.Name}"));
            }
        }
    }
}
