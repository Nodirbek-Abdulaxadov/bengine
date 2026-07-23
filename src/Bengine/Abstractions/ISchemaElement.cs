namespace Bengine.Abstractions;

/// <summary>
/// Marker for a node that participates in the database schema AST. Nodes are
/// deliberately unaware of visitors; traversal is driven externally to keep
/// model responsibilities focused on structure.
/// </summary>
public interface ISchemaElement : INamed
{
}
