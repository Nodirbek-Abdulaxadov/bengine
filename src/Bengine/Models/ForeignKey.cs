namespace Bengine.Models;

using Bengine.Abstractions;
using Bengine.Enums;

/// <summary>
/// A foreign-key constraint relating columns of the owning table to columns of a
/// referenced table. Owned by the <see cref="Table"/>; all participants are
/// referenced by name.
/// </summary>
public sealed class ForeignKey : ISchemaElement
{
    /// <inheritdoc />
    public required string Name { get; init; }

    /// <summary>Gets the ordered local column names that form the key.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>Gets the name of the referenced table.</summary>
    public required string ReferencedTable { get; init; }

    /// <summary>Gets the schema of the referenced table, or <c>null</c> for the default schema.</summary>
    public string? ReferencedSchema { get; init; }

    /// <summary>Gets the ordered referenced column names, aligned with <see cref="Columns"/>.</summary>
    public required IReadOnlyList<string> ReferencedColumns { get; init; }

    /// <summary>Gets the action taken on the referencing rows when a referenced row is deleted.</summary>
    public ReferentialAction OnDelete { get; init; } = ReferentialAction.NoAction;

    /// <summary>Gets the action taken on the referencing rows when a referenced row is updated.</summary>
    public ReferentialAction OnUpdate { get; init; } = ReferentialAction.NoAction;

    /// <summary>Gets a value indicating whether the constraint is deferrable to end of transaction.</summary>
    public bool IsDeferrable { get; init; }
}
