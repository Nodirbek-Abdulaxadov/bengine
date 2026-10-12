namespace Bengine.Introspection.Model;

/// <summary>
/// A single column predicate applied while browsing table data. The
/// <see cref="Column"/> is validated against the relation's real columns before
/// use and then quoted as an identifier; the <see cref="Value"/> (or
/// <see cref="Values"/> for <see cref="FilterOperator.In"/>) is always bound as
/// a parameter, so a condition can never inject SQL.
/// </summary>
public sealed record FilterCondition
{
    /// <summary>Gets the name of the column the predicate constrains.</summary>
    public required string Column { get; init; }

    /// <summary>Gets the comparison to apply.</summary>
    public required FilterOperator Operator { get; init; }

    /// <summary>
    /// Gets the comparison value for a single-value operator. Ignored by
    /// <see cref="FilterOperator.IsNull"/>, <see cref="FilterOperator.IsNotNull"/>,
    /// and <see cref="FilterOperator.In"/>.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>Gets the candidate values for <see cref="FilterOperator.In"/>; ignored by every other operator.</summary>
    public IReadOnlyList<object?> Values { get; init; } = [];

    /// <summary>Creates an equality condition.</summary>
    /// <param name="column">The column name.</param>
    /// <param name="value">The value to match.</param>
    /// <returns>An <see cref="FilterOperator.Equals"/> condition.</returns>
    public static FilterCondition Equals(string column, object? value) =>
        new() { Column = column, Operator = FilterOperator.Equals, Value = value };

    /// <summary>Creates a case-sensitivity-dependent substring condition.</summary>
    /// <param name="column">The column name.</param>
    /// <param name="value">The substring to search for.</param>
    /// <returns>A <see cref="FilterOperator.Contains"/> condition.</returns>
    public static FilterCondition Contains(string column, string value) =>
        new() { Column = column, Operator = FilterOperator.Contains, Value = value };

    /// <summary>Creates a set-membership condition.</summary>
    /// <param name="column">The column name.</param>
    /// <param name="values">The candidate values.</param>
    /// <returns>A <see cref="FilterOperator.In"/> condition.</returns>
    public static FilterCondition In(string column, IReadOnlyList<object?> values) =>
        new() { Column = column, Operator = FilterOperator.In, Values = values };
}
