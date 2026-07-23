namespace Bengine.Models;

using Bengine.Abstractions;
using Bengine.Enums;
using Bengine.Models.Options;
using Bengine.Models.Options.Validation;

/// <summary>
/// A single column of a table. By design a column knows only about itself: its
/// type, nullability, default, generation, and value validation. It is
/// deliberately unaware of indexes, primary keys, and foreign keys, which are
/// owned by the <see cref="Table"/>.
/// </summary>
public sealed class Column : ISchemaElement
{
    /// <inheritdoc />
    public required string Name { get; init; }

    /// <summary>Gets the column's logical type and its modifiers.</summary>
    public required ColumnTypeOptions Type { get; init; }

    /// <summary>Gets whether the column accepts <c>NULL</c>.</summary>
    public Nullability Nullability { get; init; } = Nullability.NotNull;

    /// <summary>Gets the column's default value.</summary>
    public DefaultValue Default { get; init; } = DefaultValue.None;

    /// <summary>Gets the column's auto-population configuration.</summary>
    public IdentityOptions Identity { get; init; } = IdentityOptions.None;

    /// <summary>Gets the computed-value definition, or <c>null</c> if the column is not generated.</summary>
    public GeneratedColumn? Generated { get; init; }

    /// <summary>Gets the value-level validation attached to the column.</summary>
    public ColumnValidation Validation { get; init; } = ColumnValidation.None;

    /// <summary>Gets an optional human-readable comment.</summary>
    public string? Comment { get; init; }

    /// <summary>Gets provider-specific overrides for the column.</summary>
    public ProviderOptions ProviderOptions { get; init; } = ProviderOptions.Empty;

    /// <summary>Gets a value indicating whether the column accepts <c>NULL</c> values.</summary>
    public bool IsNullable => Nullability == Nullability.Nullable;

    /// <summary>Gets a value indicating whether the column is computed.</summary>
    public bool IsGenerated => Generated is not null;
}
