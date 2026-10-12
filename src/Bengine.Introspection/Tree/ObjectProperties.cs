namespace Bengine.Introspection.Tree;

/// <summary>
/// What the Properties and SQL panels show for a selected node: a title, a list
/// of name/value properties, the object's DDL (when it has one), and — for a
/// folder — a grid listing its children.
/// </summary>
public sealed record ObjectProperties
{
    /// <summary>Gets the panel title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the object's properties as ordered name/value pairs.</summary>
    public IReadOnlyList<PropertyItem> Properties { get; init; } = [];

    /// <summary>Gets the object's DDL, or <c>null</c> when it has none.</summary>
    public string? Sql { get; init; }

    /// <summary>Gets the column headers of the children grid (folders only).</summary>
    public IReadOnlyList<string> GridColumns { get; init; } = [];

    /// <summary>Gets the rows of the children grid (folders only).</summary>
    public IReadOnlyList<IReadOnlyList<string?>> GridRows { get; init; } = [];
}

/// <summary>One name/value property of an object.</summary>
/// <param name="Name">The property name.</param>
/// <param name="Value">The property value, or <c>null</c> when unset.</param>
public sealed record PropertyItem(string Name, string? Value);
