namespace Bengine.Introspection.Tree;

/// <summary>
/// Identifies a node in the object explorer tree by what it is and where it
/// lives, so a client can hand it back to ask for the node's children or
/// properties. Scope fields are filled in as the tree descends: a database node
/// sets <see cref="Database"/>, a schema node sets <see cref="Schema"/>, and a
/// relation node (table, view, …) sets <see cref="Relation"/>, so every
/// descendant carries the full path to its owner. Folder nodes have no
/// <see cref="Name"/>.
/// </summary>
public sealed record NodeRef
{
    /// <summary>Gets the node kind.</summary>
    public required NodeKind Kind { get; init; }

    /// <summary>Gets the owning database, or <c>null</c> for server-level nodes.</summary>
    public string? Database { get; init; }

    /// <summary>Gets the owning schema, or <c>null</c> above schema level or for providers without schemas.</summary>
    public string? Schema { get; init; }

    /// <summary>
    /// Gets the owning relation (the table or view a column, index, constraint,
    /// or trigger belongs to), or the owning foreign-data wrapper for a foreign
    /// server.
    /// </summary>
    public string? Relation { get; init; }

    /// <summary>Gets the object's own name, or <c>null</c> for a folder.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets an extra discriminator for objects whose name is not unique — the
    /// identity argument list of an overloaded function, or a cast's OID.
    /// </summary>
    public string? Signature { get; init; }

    /// <summary>Gets a value indicating whether the node is a collection (folder) node.</summary>
    public bool IsFolder => Kind.ToString().EndsWith("Folder", StringComparison.Ordinal);

    /// <summary>Gets a stable key for the node, unique within one server's tree.</summary>
    public string Id => string.Join("|", Kind, Database, Schema, Relation, Name, Signature);

    /// <summary>Creates a folder of the given kind scoped to this node.</summary>
    /// <param name="kind">The folder kind.</param>
    /// <returns>A folder reference inheriting this node's scope.</returns>
    public NodeRef Folder(NodeKind kind) => this with { Kind = kind, Name = null, Signature = null };

    /// <summary>Creates an item of the given kind inside this folder.</summary>
    /// <param name="kind">The item kind.</param>
    /// <param name="name">The item name.</param>
    /// <param name="signature">An optional discriminator for non-unique names.</param>
    /// <returns>An item reference inheriting this folder's scope.</returns>
    public NodeRef Item(NodeKind kind, string name, string? signature = null) =>
        this with { Kind = kind, Name = name, Signature = signature };
}
