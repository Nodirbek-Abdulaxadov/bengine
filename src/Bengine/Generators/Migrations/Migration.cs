namespace Bengine.Generators.Migrations;

/// <summary>
/// Immutable pair of forward and reverse DDL scripts describing a single schema
/// migration.
/// </summary>
public sealed record Migration
{
    /// <summary>Gets the migration's identifying name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the DDL applied when moving the schema forward.</summary>
    public required string Up { get; init; }

    /// <summary>Gets the DDL applied when rolling the schema back.</summary>
    public required string Down { get; init; }
}
