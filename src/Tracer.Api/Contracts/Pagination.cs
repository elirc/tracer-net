using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Tracer.Api.Contracts;

/// <summary>
/// The two query parameters every bounded list endpoint accepts. Split out into
/// one record so a new list endpoint bounds itself the moment it binds this,
/// rather than each controller re-deciding what "page" means — and so the
/// defaults and the 1–100 cap live in exactly one place.
///
/// <para>
/// The cap is the point. A list read with no upper bound is a read whose cost is
/// set by however much data has accumulated, which is a denial-of-service lever
/// the moment a team has enough issues, comments, or history. <c>PageSize</c>
/// is validated to 1–100, so no single request can ask the database for an
/// unbounded number of rows.
/// </para>
/// </summary>
public record PageQuery
{
    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    public int PageSize { get; init; } = 50;
}

public static class Pagination
{
    /// <summary>
    /// Counts, pages, and returns an already-ordered, already-projected query as a
    /// <see cref="PagedResult{T}"/>. For the endpoints that build their DTO in the
    /// database with a <c>Select</c>; see the mapping overload for those that need
    /// to project in memory after loading navigations.
    /// </summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> ordered,
        PageQuery page,
        CancellationToken ct = default)
    {
        var total = await ordered.CountAsync(ct);

        var items = await ordered
            .Skip((page.Page - 1) * page.PageSize)
            .Take(page.PageSize)
            .ToListAsync(ct);

        return new PagedResult<T>(
            items,
            page.Page,
            page.PageSize,
            total,
            (int)Math.Ceiling(total / (double)page.PageSize));
    }

    /// <summary>
    /// Counts, pages, and projects an already-ordered query into a
    /// <see cref="PagedResult{TDto}"/> — the same envelope the issue search,
    /// activity feed, notifications inbox, and delivery log all return, so every
    /// list in the API pages the same way.
    ///
    /// <para>
    /// <b>Order and include before you call this.</b> The caller applies its
    /// <c>OrderBy</c> (with a stable id tiebreak) and any <c>Include</c> first; this
    /// helper only counts, skips, takes, and maps. That ordering matters twice: a
    /// page of an unordered set repeats and skips rows between requests, and EF
    /// applies <c>Skip</c>/<c>Take</c> to the joined rows rather than the roots when
    /// a collection <c>Include</c> comes after them — so includes come first.
    /// </para>
    /// </summary>
    public static async Task<PagedResult<TDto>> ToPagedResultAsync<TSource, TDto>(
        this IQueryable<TSource> ordered,
        PageQuery page,
        Func<TSource, TDto> map,
        CancellationToken ct = default)
    {
        var total = await ordered.CountAsync(ct);

        var rows = await ordered
            .Skip((page.Page - 1) * page.PageSize)
            .Take(page.PageSize)
            .ToListAsync(ct);

        return new PagedResult<TDto>(
            rows.Select(map).ToList(),
            page.Page,
            page.PageSize,
            total,
            (int)Math.Ceiling(total / (double)page.PageSize));
    }
}
