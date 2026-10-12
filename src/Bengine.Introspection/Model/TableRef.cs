namespace Bengine.Introspection.Model;

/// <summary>
/// A lightweight, provider-independent pointer to a relation discovered in a
/// live database: its optional schema, its name, and whether it is a base table
/// or a view. Discovery returns these cheaply so a caller can list a database
/// before paying to introspect or read any single relation.
/// </summary>
/// <param name="Name">The relation's name.</param>
/// <param name="Schema">The owning schema or namespace, or <c>null</c> when the provider has no schemas (SQLite) or the default is implied.</param>
/// <param name="Kind">Whether the relation is a base table or a view.</param>
/// <param name="EstimatedRowCount">An optional planner estimate of the row count; <c>null</c> when the provider does not expose one cheaply. Never treat it as exact.</param>
public sealed record TableRef(
    string Name,
    string? Schema = null,
    TableKind Kind = TableKind.Table,
    long? EstimatedRowCount = null)
{
    /// <summary>Gets the schema-qualified display name (<c>schema.name</c>, or just <c>name</c> when unqualified).</summary>
    public string QualifiedName => Schema is null ? Name : $"{Schema}.{Name}";

    /// <inheritdoc />
    public override string ToString() => QualifiedName;
}
