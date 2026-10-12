namespace Bengine.Introspection.Model;

/// <summary>
/// Distinguishes the kind of relation a <see cref="TableRef"/> points at, so a
/// data browser can treat updatable base tables and read-only views
/// differently.
/// </summary>
public enum TableKind
{
    /// <summary>An ordinary base table.</summary>
    Table,

    /// <summary>A view.</summary>
    View,
}
