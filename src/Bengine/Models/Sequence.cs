namespace Bengine.Models;

using Bengine.Abstractions;
using Bengine.Enums;

/// <summary>
/// A standalone sequence generator. Owned by the <see cref="DatabaseSchema"/>.
/// </summary>
public sealed class Sequence : ISchemaObject
{
    /// <inheritdoc />
    public required string Name { get; init; }

    /// <inheritdoc />
    public string? Schema { get; init; }

    /// <summary>Gets the integral type produced by the sequence.</summary>
    public DbDataType DataType { get; init; } = DbDataType.BigInt;

    /// <summary>Gets the first value produced.</summary>
    public long StartValue { get; init; } = 1;

    /// <summary>Gets the step between successive values.</summary>
    public long Increment { get; init; } = 1;

    /// <summary>Gets the minimum producible value, if bounded.</summary>
    public long? MinValue { get; init; }

    /// <summary>Gets the maximum producible value, if bounded.</summary>
    public long? MaxValue { get; init; }

    /// <summary>Gets the number of values pre-allocated for performance, if any.</summary>
    public int? Cache { get; init; }

    /// <summary>Gets a value indicating whether the sequence wraps around at its bound.</summary>
    public bool Cycle { get; init; }
}
