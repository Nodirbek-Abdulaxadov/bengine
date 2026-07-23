namespace Bengine.Models;

using Bengine.Abstractions;

/// <summary>
/// The primary key of a table. Owned by the <see cref="Table"/>; references its
/// member columns by name so columns remain unaware of key membership.
/// </summary>
public sealed class PrimaryKey : INamed
{
    /// <summary>
    /// Gets the constraint name. When omitted, generators derive a provider
    /// default (for example <c>pk_&lt;table&gt;</c>).
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the ordered names of the columns comprising the key.</summary>
    public required IReadOnlyList<string> ColumnNames { get; init; }

    /// <summary>Gets a value indicating whether the key spans more than one column.</summary>
    public bool IsComposite => ColumnNames.Count > 1;
}
