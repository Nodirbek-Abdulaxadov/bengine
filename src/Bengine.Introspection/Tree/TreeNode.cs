namespace Bengine.Introspection.Tree;

/// <summary>
/// One node as shown in the object explorer: its reference, display label,
/// optional secondary text and badge, whether it can be expanded, and — for a
/// folder — how many children it holds.
/// </summary>
public sealed record TreeNode
{
    /// <summary>Gets the node's reference, passed back to load its children or properties.</summary>
    public required NodeRef Ref { get; init; }

    /// <summary>Gets the display label.</summary>
    public required string Label { get; init; }

    /// <summary>Gets optional secondary text shown dimmed after the label (a column's type, an extension's version).</summary>
    public string? Detail { get; init; }

    /// <summary>Gets an optional short badge (for example <c>PK</c> on a key column).</summary>
    public string? Badge { get; init; }

    /// <summary>Gets a value indicating whether the node can be expanded.</summary>
    public bool HasChildren { get; init; }

    /// <summary>Gets the number of children of a folder node, or <c>null</c> when unknown or not a folder.</summary>
    public long? Count { get; init; }

    /// <summary>Gets a value indicating whether the node is a collection (folder) node.</summary>
    public bool IsFolder => Ref.IsFolder;

    /// <summary>Gets the node's stable key.</summary>
    public string Id => Ref.Id;
}
