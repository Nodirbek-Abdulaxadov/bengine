namespace Bengine.Introspection.Model;

/// <summary>
/// A request to read one page of rows from a relation: which page, how large,
/// the filters to apply, and the ordering. Paging is one-based and expressed as
/// a page number plus size so the same query is natural for both a desktop grid
/// and a web table; the reader translates it to the provider's native
/// <c>LIMIT/OFFSET</c> or <c>OFFSET/FETCH</c>.
/// </summary>
public sealed record DataQuery
{
    /// <summary>The largest page size a caller may request, guarding against unbounded reads.</summary>
    public const int MaxPageSize = 1000;

    /// <summary>The default page size when none is specified.</summary>
    public const int DefaultPageSize = 100;

    private readonly int _page = 1;
    private readonly int _pageSize = DefaultPageSize;

    /// <summary>Gets the one-based page number. Values below 1 are clamped to 1.</summary>
    public int Page
    {
        get => _page;
        init => _page = value < 1 ? 1 : value;
    }

    /// <summary>Gets the page size. Values are clamped to the range 1..<see cref="MaxPageSize"/>.</summary>
    public int PageSize
    {
        get => _pageSize;
        init => _pageSize = Math.Clamp(value, 1, MaxPageSize);
    }

    /// <summary>Gets the filters combined with <c>AND</c>; empty means no filtering.</summary>
    public IReadOnlyList<FilterCondition> Filters { get; init; } = [];

    /// <summary>Gets the ordering terms; empty means provider-default ordering.</summary>
    public IReadOnlyList<SortSpec> Sort { get; init; } = [];

    /// <summary>
    /// Gets a free-form SQL condition typed into the data editor's <c>WHERE</c>
    /// box, combined with <see cref="Filters"/> by <c>AND</c>. It is the user's own
    /// SQL, run verbatim like a console statement, so it is only as safe as the
    /// connection's permissions.
    /// </summary>
    public string? WhereSql { get; init; }

    /// <summary>
    /// Gets a free-form SQL ordering typed into the data editor's <c>ORDER BY</c>
    /// box; when set it takes precedence over <see cref="Sort"/>. Run verbatim.
    /// </summary>
    public string? OrderBySql { get; init; }

    /// <summary>
    /// Gets a value indicating whether to run a matching <c>COUNT(*)</c> so the
    /// page carries the total row count. Off by default because counting a large
    /// filtered relation can be as costly as the page read itself.
    /// </summary>
    public bool IncludeTotalCount { get; init; }

    /// <summary>Gets the zero-based row offset implied by <see cref="Page"/> and <see cref="PageSize"/>.</summary>
    public int Offset => (Page - 1) * PageSize;
}
