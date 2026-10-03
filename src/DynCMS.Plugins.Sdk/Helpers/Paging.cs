using Microsoft.AspNetCore.WebUtilities;

namespace DynCMS.Core.Helpers;

/// <summary>
/// One page of a longer list, with the numbers a pager needs. <see cref="Paging.Page{T}"/> makes one from any
/// list; the <c>paginate</c> Liquid filter and the <c>CmsPager</c> component render it.
/// </summary>
public interface IPagedList
{
    /// <summary>The current page, 1-based, always within 1..<see cref="PageCount"/>.</summary>
    int Page { get; }
    int PageSize { get; }
    /// <summary>How many pages there are; at least 1, even for an empty list.</summary>
    int PageCount { get; }
    /// <summary>How many items there are in the whole list.</summary>
    int TotalCount { get; }
    /// <summary>The 1-based position of the first item on this page in the whole list (0 when the list is empty).</summary>
    int First { get; }
    /// <summary>The 1-based position of the last item on this page in the whole list (0 when the list is empty).</summary>
    int Last { get; }
    bool HasPrevious { get; }
    bool HasNext { get; }
    int? PreviousPage { get; }
    int? NextPage { get; }
}

/// <inheritdoc cref="IPagedList"/>
public sealed class PagedList<T> : IPagedList
{
    internal PagedList(IReadOnlyList<T> items, int page, int pageSize, int pageCount, int totalCount)
    {
        Items = items;
        Page = page;
        PageSize = pageSize;
        PageCount = pageCount;
        TotalCount = totalCount;
    }

    /// <summary>The items on this page.</summary>
    public IReadOnlyList<T> Items { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int PageCount { get; }
    public int TotalCount { get; }
    public int First => TotalCount == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int Last => TotalCount == 0 ? 0 : Math.Min(Page * PageSize, TotalCount);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < PageCount;
    public int? PreviousPage => HasPrevious ? Page - 1 : null;
    public int? NextPage => HasNext ? Page + 1 : null;
}

/// <summary>
/// Paging helpers shared by component templates (<c>CmsPager</c>) and stored templates (the <c>paginate</c>
/// filter): slicing a list into a page, reading the page number from a query string, and building the URL of
/// another page while keeping the rest of the query string (<c>?preview=true</c>, a filter, …).
/// </summary>
public static class Paging
{
    /// <summary>The query string parameter that carries the page number: <c>/blog?page=2</c>.</summary>
    public const string PageParameter = "page";

    /// <summary>
    /// Takes page <paramref name="page"/> of <paramref name="source"/>, <paramref name="pageSize"/> items to a page.
    /// The page number is clamped into range, so <c>?page=0</c> shows the first page and <c>?page=99</c> the last
    /// one instead of an empty page; a missing number means the first page.
    /// </summary>
    public static PagedList<T> Page<T>(IEnumerable<T> source, int pageSize, int? page)
    {
        var all = source as IReadOnlyList<T> ?? source.ToList();
        pageSize = Math.Max(1, pageSize);
        var pageCount = Math.Max(1, (int)Math.Ceiling(all.Count / (double)pageSize));
        var current = Math.Clamp(page ?? 1, 1, pageCount);
        var items = all.Skip((current - 1) * pageSize).Take(pageSize).ToList();
        return new PagedList<T>(items, current, pageSize, pageCount, all.Count);
    }

    /// <summary>The page number in a query string value; null when it is missing or not a number.</summary>
    public static int? ParsePage(string? value) =>
        int.TryParse(value, out var n) ? n : null;

    /// <summary>The page number in the query string of <paramref name="uri"/>; null when there is none.</summary>
    public static int? ParsePage(Uri uri, string parameter = PageParameter) =>
        QueryHelpers.ParseQuery(uri.Query).TryGetValue(parameter, out var v) ? ParsePage(v.ToString()) : null;

    /// <summary>
    /// The URL of page <paramref name="page"/> of the list at <paramref name="uri"/>: the same path and query
    /// string with the page parameter replaced. The first page has no page parameter, so every page has one
    /// canonical address.
    /// </summary>
    public static string PageUrl(Uri uri, int page, string parameter = PageParameter) =>
        PageUrl(uri.AbsolutePath, QueryHelpers.ParseQuery(uri.Query).Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value.ToString())), page, parameter);

    /// <summary>The URL of page <paramref name="page"/> of the list at <paramref name="path"/>, keeping the other parameters in <paramref name="query"/>.</summary>
    public static string PageUrl(string path, IEnumerable<KeyValuePair<string, string?>>? query, int page, string parameter = PageParameter)
    {
        var kept = (query ?? [])
            .Where(kv => !string.Equals(kv.Key, parameter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (page > 1) kept.Add(new KeyValuePair<string, string?>(parameter, page.ToString()));
        return QueryHelpers.AddQueryString(path, kept);
    }

    /// <summary>
    /// The page numbers a compact pager shows: the first and last page, the current one and <paramref name="window"/>
    /// pages either side of it. A <c>null</c> stands for the pages skipped in between (rendered as an ellipsis).
    /// </summary>
    public static IReadOnlyList<int?> PageNumbers(int page, int pageCount, int window = 2)
    {
        if (pageCount <= 2 * window + 5)
            return Enumerable.Range(1, pageCount).Select(n => (int?)n).ToList();

        var result = new List<int?> { 1 };
        var from = Math.Max(2, page - window);
        var to = Math.Min(pageCount - 1, page + window);
        if (from > 2) result.Add(null);
        for (var n = from; n <= to; n++) result.Add(n);
        if (to < pageCount - 1) result.Add(null);
        result.Add(pageCount);
        return result;
    }
}
