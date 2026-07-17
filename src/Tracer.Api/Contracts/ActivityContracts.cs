using System.ComponentModel.DataAnnotations;
using Tracer.Domain.Entities;

namespace Tracer.Api.Contracts;

public record ActivityDto(
    Guid Id,
    Guid TeamId,
    Guid IssueId,
    string Identifier,
    string IssueTitle,
    ActivityType Type,
    string? Field,
    string? OldValue,
    string? NewValue,
    Guid? ActorId,
    string Actor,
    DateTimeOffset CreatedAt);

/// <summary>
/// Filters for a team's feed. All optional, combining with AND — the same
/// contract the issue search already uses, so there is one way to filter in this
/// API rather than two.
/// </summary>
public record ActivityFeedQuery
{
    /// <summary>Narrow to one issue's history, including an issue since deleted.</summary>
    public Guid? IssueId { get; init; }

    public ActivityType? Type { get; init; }

    /// <summary>Handle of the person who acted, compared case-insensitively.</summary>
    [MaxLength(100)]
    public string? Actor { get; init; }

    /// <summary>Inclusive lower bound on <see cref="ActivityDto.CreatedAt"/>.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Exclusive upper bound, so [since, until) tiles without overlap — as cycles do.</summary>
    public DateTimeOffset? Until { get; init; }

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    public int PageSize { get; init; } = 50;
}

public static class ActivityMappings
{
    /// <summary>
    /// Requires <see cref="Activity.Team"/> to be loaded — the identifier is
    /// rendered from the live team key so a renamed team renames its history, as
    /// identifiers do everywhere else.
    /// </summary>
    public static ActivityDto ToDto(this Activity activity) => new(
        activity.Id,
        activity.TeamId,
        activity.IssueId,
        $"{activity.Team!.Key}-{activity.IssueNumber}",
        activity.IssueTitle,
        activity.Type,
        activity.Field,
        activity.OldValue,
        activity.NewValue,
        activity.ActorId,
        activity.ActorHandle,
        activity.CreatedAt);
}
