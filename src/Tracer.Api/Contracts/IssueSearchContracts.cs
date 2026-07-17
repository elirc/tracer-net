using System.ComponentModel.DataAnnotations;
using Tracer.Domain.Entities;

namespace Tracer.Api.Contracts;

public enum IssueSortField
{
    Updated = 0,
    Created = 1,
    Priority = 2,
    Number = 3,
    Title = 4,
    Position = 5,
}

public enum SortDirection
{
    Asc = 0,
    Desc = 1,
}

/// <summary>
/// Which issues to show and in what order. Every filter is optional and they
/// combine with AND; unset filters are absent, not wildcards.
///
/// <para>
/// This is the half of a search that is worth naming and keeping — it is what a
/// saved view stores as its rules. Note what is <b>not</b> here: no team, and no
/// paging. A saved view already belongs to a team, so rules that could also name
/// one would give "which team's issues does this view show?" two answers that
/// can disagree; leaving <see cref="IssueSearchQuery.TeamId"/> out of the rules
/// means the question can only be asked of the view. Paging is a property of a
/// request, not of a view: the same view is page 1 for one caller and page 3 for
/// the next.
/// </para>
/// </summary>
public record IssueFilter
{
    public Guid? ProjectId { get; init; }
    public Guid? StateId { get; init; }
    public Guid? CycleId { get; init; }
    public Guid? LabelId { get; init; }

    /// <summary>Exact assignee handle, compared case-insensitively.</summary>
    [MaxLength(100)]
    public string? Assignee { get; init; }

    public IssuePriority? Priority { get; init; }

    /// <summary>Free-text substring matched against title and description.</summary>
    [MaxLength(200)]
    public string? Q { get; init; }

    public IssueSortField Sort { get; init; } = IssueSortField.Updated;

    public SortDirection Order { get; init; } = SortDirection.Desc;
}

/// <summary>
/// Filters for <c>GET /api/issues</c>: an <see cref="IssueFilter"/> plus the two
/// things a stored view has no business remembering — the team to narrow to, and
/// where the caller is in the results.
/// </summary>
public record IssueSearchQuery : IssueFilter
{
    public Guid? TeamId { get; init; }

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    public int PageSize { get; init; } = 25;
}

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total, int TotalPages);

/// <summary>
/// Moves an issue within a column, or to another column. Omit both neighbours
/// to append to the end of the target column.
/// </summary>
public record ReorderIssueRequest(
    Guid? StateId = null,
    Guid? AfterIssueId = null,
    Guid? BeforeIssueId = null);
