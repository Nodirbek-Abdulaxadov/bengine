namespace Bengine.Introspection.Tree;

/// <summary>
/// A provider's object-explorer tree over one open connection: the children of
/// any node, loaded lazily one level at a time, and the properties and DDL of an
/// object node. The connection must point at the database the node belongs to —
/// <see cref="ObjectExplorer"/> takes care of opening the right one.
/// </summary>
public interface IObjectTree
{
    /// <summary>Lists the children of a node.</summary>
    /// <param name="node">The node to expand.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The child nodes, folders first in display order.</returns>
    Task<IReadOnlyList<TreeNode>> GetChildrenAsync(NodeRef node, CancellationToken cancellationToken = default);

    /// <summary>Reads the properties and DDL of an object node.</summary>
    /// <param name="node">The object node.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The object's properties.</returns>
    Task<ObjectProperties> GetPropertiesAsync(NodeRef node, CancellationToken cancellationToken = default);
}
