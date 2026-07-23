namespace Bengine.Abstractions;

/// <summary>
/// Implemented by every schema element that carries a stable, addressable name.
/// </summary>
public interface INamed
{
    /// <summary>Gets the object's name as declared in the schema.</summary>
    string Name { get; }
}
