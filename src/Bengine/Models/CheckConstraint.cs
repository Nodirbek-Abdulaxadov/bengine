namespace Bengine.Models;

using Bengine.Abstractions;

/// <summary>
/// A table-level check constraint expressed as a boolean SQL predicate. Owned by
/// the <see cref="Table"/>.
/// </summary>
public sealed class CheckConstraint : ISchemaElement
{
    /// <inheritdoc />
    public required string Name { get; init; }

    /// <summary>Gets the boolean expression rows must satisfy.</summary>
    public required string Expression { get; init; }
}
