namespace Bengine.Visitors;

using Bengine.Enums;
using Bengine.Models;
using Bengine.Validation;

/// <summary>
/// A visitor that validates each table it encounters against a target provider
/// and accumulates the resulting diagnostics. The aggregated
/// <see cref="Result"/> is available once traversal completes.
/// </summary>
public sealed class ValidationVisitor : SchemaVisitorBase
{
    private readonly TableValidator _validator;
    private readonly DatabaseProvider _provider;
    private readonly List<ValidationMessage> _messages = [];

    /// <summary>Initializes the visitor for a provider using the default validator.</summary>
    /// <param name="provider">The provider to validate against.</param>
    public ValidationVisitor(DatabaseProvider provider)
        : this(provider, new TableValidator())
    {
    }

    /// <summary>Initializes the visitor for a provider using an explicit validator.</summary>
    /// <param name="provider">The provider to validate against.</param>
    /// <param name="validator">The table validator to apply.</param>
    public ValidationVisitor(DatabaseProvider provider, TableValidator validator)
    {
        _provider = provider;
        _validator = validator;
    }

    /// <summary>Gets the aggregated validation result across every visited table.</summary>
    public ValidationResult Result => ValidationResult.From(_messages);

    /// <inheritdoc />
    public override void VisitTable(Table table) =>
        _messages.AddRange(_validator.Validate(table, _provider).Messages);
}
