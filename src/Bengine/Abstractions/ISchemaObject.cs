namespace Bengine.Abstractions;

using Bengine.Enums;

/// <summary>
/// Implemented by top-level, optionally schema-qualified database objects such
/// as tables, views, sequences, and triggers.
/// </summary>
public interface ISchemaObject : ISchemaElement
{
    /// <summary>Gets the owning schema (namespace) name, or <c>null</c> for the default schema.</summary>
    string? Schema { get; }
}
