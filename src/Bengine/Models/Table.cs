namespace Bengine.Models;

using Bengine.Abstractions;
using Bengine.Models.Options;

/// <summary>
/// A table in the schema. The table is the sole owner of its structural
/// constraints — primary key, foreign keys, indexes, and check constraints —
/// while its columns remain unaware of them.
/// </summary>
public sealed class Table : ISchemaObject
{
    /// <inheritdoc />
    public required string Name { get; init; }

    /// <inheritdoc />
    public string? Schema { get; init; }

    /// <summary>Gets the ordered columns of the table.</summary>
    public required IReadOnlyList<Column> Columns { get; init; }

    /// <summary>Gets the primary key, or <c>null</c> if the table has none.</summary>
    public PrimaryKey? PrimaryKey { get; init; }

    /// <summary>Gets the foreign keys owned by the table.</summary>
    public IReadOnlyList<ForeignKey> ForeignKeys { get; init; } = [];

    /// <summary>Gets the indexes owned by the table.</summary>
    public IReadOnlyList<Index> Indexes { get; init; } = [];

    /// <summary>Gets the check constraints owned by the table.</summary>
    public IReadOnlyList<CheckConstraint> CheckConstraints { get; init; } = [];

    /// <summary>Gets an optional human-readable comment.</summary>
    public string? Comment { get; init; }

    /// <summary>Gets provider-specific overrides for the table.</summary>
    public ProviderOptions ProviderOptions { get; init; } = ProviderOptions.Empty;
}
