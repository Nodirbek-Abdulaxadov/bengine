namespace Bengine.Validation;

using Bengine.Enums;
using Bengine.Models;

/// <summary>
/// Validates an entire schema: each table is validated individually, table names
/// are checked for uniqueness, and every foreign key is checked to reference a
/// declared table.
/// </summary>
public sealed class DatabaseValidator : IValidator<DatabaseSchema>
{
    private readonly TableValidator _tableValidator;

    /// <summary>Initializes a validator with the default table validator.</summary>
    public DatabaseValidator()
        : this(new TableValidator())
    {
    }

    /// <summary>Initializes a validator with an explicit table validator.</summary>
    /// <param name="tableValidator">The table validator to delegate to.</param>
    public DatabaseValidator(TableValidator tableValidator) => _tableValidator = tableValidator;

    /// <inheritdoc />
    public ValidationResult Validate(DatabaseSchema target, DatabaseProvider provider)
    {
        var messages = new List<ValidationMessage>();
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in target.Tables)
        {
            var qualified = Qualify(table.Schema, table.Name);
            if (!tableNames.Add(qualified))
            {
                messages.Add(ValidationMessage.Error(
                    "database.table.duplicate",
                    $"Duplicate table name '{qualified}'.",
                    qualified));
            }

            messages.AddRange(_tableValidator.Validate(table, provider).Messages);
        }

        foreach (var table in target.Tables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                var referenced = Qualify(fk.ReferencedSchema, fk.ReferencedTable);
                if (!tableNames.Contains(referenced))
                {
                    messages.Add(ValidationMessage.Error(
                        "database.foreignKey.unknownTable",
                        $"Foreign key '{fk.Name}' references unknown table '{referenced}'.",
                        $"{table.Name}.{fk.Name}"));
                }
            }
        }

        return ValidationResult.From(messages);
    }

    private static string Qualify(string? schema, string name) =>
        schema is null ? name : $"{schema}.{name}";
}
