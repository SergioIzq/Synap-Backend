namespace Synap.Domain;

/// <summary>One page of results plus the total number of matches (specs/knowledge-vault "Results are paginated").</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public bool HasNextPage => Page * PageSize < TotalCount;

    public static PagedResult<T> Empty(int page, int pageSize) => new([], page, pageSize, 0);
}
