namespace Bengine.Models.Options;

using Bengine.Enums;

/// <summary>
/// Immutable description of a column's logical type together with the structural
/// modifiers (length, precision, scale, character handling, storage) that refine
/// it. The concrete provider spelling is resolved later through metadata and
/// mapping; this object never stores a raw SQL type name.
/// </summary>
public sealed record ColumnTypeOptions
{
    /// <summary>Gets the logical data type.</summary>
    public required DbDataType DataType { get; init; }

    /// <summary>Gets the bounded length in characters or bytes, if applicable.</summary>
    public int? Length { get; init; }

    /// <summary>Gets the total number of significant digits, if applicable.</summary>
    public int? Precision { get; init; }

    /// <summary>Gets the number of fractional digits, if applicable.</summary>
    public int? Scale { get; init; }

    /// <summary>Gets the fractional-seconds precision for temporal types, if applicable.</summary>
    public int? FractionalSecondsPrecision { get; init; }

    /// <summary>Gets a value indicating whether the numeric type is unsigned.</summary>
    public bool IsUnsigned { get; init; }

    /// <summary>Gets the character set for textual types.</summary>
    public Charset Charset { get; init; } = Charset.Default;

    /// <summary>Gets the collation for textual types.</summary>
    public Collation Collation { get; init; } = Collation.Default;

    /// <summary>Gets the physical storage strategy hint.</summary>
    public StorageStrategy Storage { get; init; } = StorageStrategy.Default;

    /// <summary>
    /// Creates a type option with no modifiers for the supplied logical type.
    /// </summary>
    /// <param name="dataType">The logical data type.</param>
    /// <returns>A modifier-free <see cref="ColumnTypeOptions"/>.</returns>
    public static ColumnTypeOptions Of(DbDataType dataType) => new() { DataType = dataType };
}
