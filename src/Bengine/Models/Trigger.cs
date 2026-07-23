namespace Bengine.Models;

using Bengine.Abstractions;
using Bengine.Enums;

/// <summary>
/// A trigger attached to a table. Owned by the <see cref="DatabaseSchema"/>. The
/// <see cref="Body"/> is provider procedural code treated as an opaque body.
/// </summary>
public sealed class Trigger : ISchemaObject
{
    /// <inheritdoc />
    public required string Name { get; init; }

    /// <inheritdoc />
    public string? Schema { get; init; }

    /// <summary>Gets the name of the table the trigger is attached to.</summary>
    public required string TableName { get; init; }

    /// <summary>Gets when the trigger fires relative to the operation.</summary>
    public required TriggerTiming Timing { get; init; }

    /// <summary>Gets the set of events that activate the trigger.</summary>
    public required TriggerEvent Events { get; init; }

    /// <summary>Gets the granularity at which the trigger action runs.</summary>
    public TriggerScope Scope { get; init; } = TriggerScope.Row;

    /// <summary>Gets an optional predicate that must hold for the trigger to fire.</summary>
    public string? WhenCondition { get; init; }

    /// <summary>Gets the procedural body executed when the trigger fires.</summary>
    public required string Body { get; init; }
}
