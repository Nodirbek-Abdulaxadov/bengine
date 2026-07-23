namespace Bengine.Validation;

using Bengine.Enums;
using Bengine.Metadata;
using Bengine.Models;
using Bengine.Validation.Rules;

/// <summary>
/// Validates a single column by composing the independent
/// <see cref="IColumnRule"/> rules. All type knowledge comes from
/// <see cref="DbDataTypeRegistry"/>; the validator itself hardcodes no type
/// facts.
/// </summary>
public sealed class ColumnValidator : IValidator<Column>
{
    private readonly IReadOnlyList<IColumnRule> _rules;

    /// <summary>Initializes a validator with the default rule set.</summary>
    public ColumnValidator()
        : this(
            new ProviderSupportRule(),
            new CapabilityRule(),
            new LengthRule(),
            new PrecisionRule(),
            new ScaleRule())
    {
    }

    /// <summary>Initializes a validator with an explicit rule set.</summary>
    /// <param name="rules">The rules to apply, in order.</param>
    public ColumnValidator(params IColumnRule[] rules) => _rules = rules;

    /// <inheritdoc />
    public ValidationResult Validate(Column target, DatabaseProvider provider) =>
        Validate(target, provider, target.Name);

    /// <summary>Validates a column using an explicit diagnostic path.</summary>
    /// <param name="target">The column to validate.</param>
    /// <param name="provider">The target provider.</param>
    /// <param name="path">The dotted path locating the column.</param>
    /// <returns>The aggregated validation result.</returns>
    public ValidationResult Validate(Column target, DatabaseProvider provider, string path)
    {
        if (!DbDataTypeRegistry.TryGet(target.Type.DataType, out var metadata) || metadata is null)
        {
            return ValidationResult.From([
                ValidationMessage.Error(
                    "column.type.unknown",
                    $"No metadata is registered for type '{target.Type.DataType}'.",
                    path),
            ]);
        }

        return ValidationResult.From(
            _rules.SelectMany(rule => rule.Evaluate(target, metadata, provider, path)));
    }
}
