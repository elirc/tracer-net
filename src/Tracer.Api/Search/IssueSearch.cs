using Microsoft.EntityFrameworkCore;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;

namespace Tracer.Api.Search;

/// <summary>
/// The one place an <see cref="IssueFilter"/> becomes SQL.
///
/// <para>
/// Search, saved views, and export are three ways to ask the same question, so
/// they ask it through this class rather than each building their own
/// <c>Where</c> chain. The alternative — a second, "simpler" filter chain for
/// views — is how a product ends up with a search that escapes LIKE wildcards
/// and a view that does not, and nobody notices until a view named "50% done"
/// matches every issue in the workspace.
/// </para>
/// <para>
/// Callers hand in the queryable they have already scoped (to a team, or to the
/// teams the caller belongs to). This class narrows and orders; it never decides
/// who may see what.
/// </para>
/// </summary>
public static class IssueSearch
{
    /// <summary>Escape character for LIKE patterns; free-text input may contain % or _.</summary>
    private const string LikeEscape = "\\";

    public static IQueryable<Issue> ApplyFilters(IQueryable<Issue> issues, IssueFilter filter)
    {
        if (filter.ProjectId is { } projectId)
        {
            issues = issues.Where(i => i.ProjectId == projectId);
        }

        if (filter.StateId is { } stateId)
        {
            issues = issues.Where(i => i.StateId == stateId);
        }

        if (filter.CycleId is { } cycleId)
        {
            issues = issues.Where(i => i.CycleId == cycleId);
        }

        if (filter.LabelId is { } labelId)
        {
            issues = issues.Where(i => i.Labels.Any(l => l.Id == labelId));
        }

        if (filter.Priority is { } priority)
        {
            issues = issues.Where(i => i.Priority == priority);
        }

        if (!string.IsNullOrWhiteSpace(filter.Assignee))
        {
            var assignee = filter.Assignee.Trim().ToLower();
            issues = issues.Where(i => i.Assignee != null && i.Assignee.ToLower() == assignee);
        }

        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var pattern = $"%{EscapeLike(filter.Q.Trim())}%";
            issues = issues.Where(i =>
                EF.Functions.Like(i.Title, pattern, LikeEscape) ||
                (i.Description != null && EF.Functions.Like(i.Description, pattern, LikeEscape)));
        }

        return issues;
    }

    /// <summary>
    /// Ties are broken by id so that paging through a sorted result cannot
    /// repeat or skip rows when several issues share a sort key.
    /// </summary>
    public static IQueryable<Issue> ApplySort(IQueryable<Issue> issues, IssueFilter filter)
    {
        var desc = filter.Order == SortDirection.Desc;

        IOrderedQueryable<Issue> sorted = filter.Sort switch
        {
            IssueSortField.Created => desc
                ? issues.OrderByDescending(i => i.CreatedAt)
                : issues.OrderBy(i => i.CreatedAt),
            IssueSortField.Number => desc
                ? issues.OrderByDescending(i => i.Number)
                : issues.OrderBy(i => i.Number),
            IssueSortField.Title => desc
                ? issues.OrderByDescending(i => i.Title)
                : issues.OrderBy(i => i.Title),
            // "No priority" is an absence, not the lowest urgency, so it sorts
            // last ascending rather than jumping ahead of Urgent.
            IssueSortField.Priority => desc
                ? issues.OrderByDescending(i => i.Priority == IssuePriority.None ? 5 : (int)i.Priority)
                : issues.OrderBy(i => i.Priority == IssuePriority.None ? 5 : (int)i.Priority),
            // Board order: state columns left to right, then rank within a column.
            IssueSortField.Position => desc
                ? issues.OrderByDescending(i => i.State!.Position).ThenByDescending(i => i.Position)
                : issues.OrderBy(i => i.State!.Position).ThenBy(i => i.Position),
            _ => desc
                ? issues.OrderByDescending(i => i.UpdatedAt)
                : issues.OrderBy(i => i.UpdatedAt),
        };

        return sorted.ThenBy(i => i.Id);
    }

    /// <summary>
    /// Filters, orders, counts, and pages in one go — the whole read that both
    /// <c>GET /api/issues</c> and executing a saved view perform.
    ///
    /// The count runs on the filtered query before ordering and paging, so
    /// <c>total</c> describes the whole result set rather than the page.
    /// </summary>
    public static async Task<PagedResult<IssueDto>> PageAsync(
        IQueryable<Issue> issues,
        IssueFilter filter,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var filtered = ApplyFilters(issues, filter);
        var total = await filtered.CountAsync(ct);

        var results = await LoadForDto(ApplySort(filtered, filter))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<IssueDto>(
            results.Select(i => i.ToDto(i.Team!.Key, i.State!.Name)).ToList(),
            page,
            pageSize,
            total,
            (int)Math.Ceiling(total / (double)pageSize));
    }

    /// <summary>
    /// Pulls in everything <see cref="IssueMappings.ToDto"/> reads.
    ///
    /// The <c>Include</c>s come before any <c>Skip</c>/<c>Take</c> a caller adds:
    /// on a collection include, EF applies paging to the joined rows rather than
    /// to the issues, so an issue with three labels can eat three slots of a
    /// page. Include first, page second.
    /// </summary>
    public static IQueryable<Issue> LoadForDto(IQueryable<Issue> issues) => issues
        .Include(i => i.Team)
        .Include(i => i.State)
        .Include(i => i.Labels);

    private static string EscapeLike(string value) => value
        .Replace(LikeEscape, LikeEscape + LikeEscape)
        .Replace("%", LikeEscape + "%")
        .Replace("_", LikeEscape + "_");
}
