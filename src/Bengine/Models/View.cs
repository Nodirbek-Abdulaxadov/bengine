namespace Bengine.Models;

using Bengine.Abstractions;

/// <summary>
/// A database view defined by a query. Owned by the <see cref="DatabaseSchema"/>.
/// The <see cref="Definition"/> is provider SQL; the framework treats it as an
/// opaque body and does not parse it.
/// </summary>
public sealed class View : ISchemaObject
{
    /// <inheritdoc />
    public required string Name { get; init; }

    /// <inheritdoc />
    public string? Schema { get; init; }

    /// <summary>Gets the query defining the view.</summary>
    public required string Definition { get; init; }

    /// <summary>Gets explicit output column names, or an empty list to infer them from the query.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Gets a value indicating whether the view is materialized.</summary>
    public bool IsMaterialized { get; init; }

    /// <summary>Gets an optional human-readable comment.</summary>
    public string? Comment { get; init; }
}
