namespace Bengine.Introspection.Model;

using Bengine.Enums;

/// <summary>
/// One ordering term applied while browsing table data: a column and the
/// direction to sort it. The column is validated against the relation and quoted
/// as an identifier before use.
/// </summary>
/// <param name="Column">The column to order by.</param>
/// <param name="Direction">The sort direction.</param>
public sealed record SortSpec(string Column, SortDirection Direction = SortDirection.Ascending);
