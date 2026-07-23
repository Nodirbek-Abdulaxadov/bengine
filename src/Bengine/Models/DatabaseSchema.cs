namespace Bengine.Models;

using Bengine.Abstractions;

/// <summary>
/// The root of the schema AST. The database is the sole owner of its top-level
/// objects — tables, views, sequences, and triggers — and is fully
/// provider-independent.
/// </summary>
public sealed class DatabaseSchema : INamed
{
    /// <inheritdoc />
    public required string Name { get; init; }

    /// <summary>Gets the tables defined in the schema.</summary>
    public IReadOnlyList<Table> Tables { get; init; } = [];

    /// <summary>Gets the views defined in the schema.</summary>
    public IReadOnlyList<View> Views { get; init; } = [];

    /// <summary>Gets the sequences defined in the schema.</summary>
    public IReadOnlyList<Sequence> Sequences { get; init; } = [];

    /// <summary>Gets the triggers defined in the schema.</summary>
    public IReadOnlyList<Trigger> Triggers { get; init; } = [];

    /// <summary>Gets an optional human-readable comment describing the schema.</summary>
    public string? Comment { get; init; }
}
