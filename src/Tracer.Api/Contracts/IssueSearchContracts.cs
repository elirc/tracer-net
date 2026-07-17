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
/// Filters for <c>GET /api/issues</c>. Every filter is optional and they
/// combine with AND. Unset filters are absent, not wildcards.
/// </summary>
public record IssueSearchQuery
{
    public Guid? TeamId { get; init; }
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
