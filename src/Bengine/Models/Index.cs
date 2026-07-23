namespace Bengine.Models;

using Bengine.Abstractions;
using Bengine.Enums;

/// <summary>
/// An index defined on a table. Owned by the <see cref="Table"/>; references its
/// key columns by name via <see cref="IndexColumn"/>.
/// </summary>
public sealed class Index : ISchemaElement
{
    /// <inheritdoc />
    public required string Name { get; init; }

    /// <summary>Gets the logical role of the index.</summary>
    public IndexType Type { get; init; } = IndexType.NonUnique;

    /// <summary>Gets the physical access method backing the index.</summary>
    public IndexMethod Method { get; init; } = IndexMethod.BTree;

    /// <summary>Gets the ordered key columns of the index.</summary>
    public required IReadOnlyList<IndexColumn> Columns { get; init; }

    /// <summary>Gets an optional predicate restricting a partial index, or <c>null</c> for a full index.</summary>
    public string? Filter { get; init; }

    /// <summary>Gets an optional human-readable comment.</summary>
    public string? Comment { get; init; }

    /// <summary>Gets a value indicating whether the index enforces uniqueness.</summary>
    public bool IsUnique => Type == IndexType.Unique;

    /// <summary>Gets a value indicating whether the index is partial (filtered).</summary>
    public bool IsPartial => Filter is not null;
}
