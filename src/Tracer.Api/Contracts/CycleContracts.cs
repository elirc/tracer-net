using System.ComponentModel.DataAnnotations;
using Tracer.Domain;
using Tracer.Domain.Entities;

namespace Tracer.Api.Contracts;

public record CycleDto(
    Guid Id,
    Guid TeamId,
    int Number,
    string? Name,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    CycleStatus Status);

/// <summary>
/// Progress roll-up for a cycle. Canceled issues are dropped from the scope
/// entirely: work that was called off should not count against the team's
/// completion rate, but it is reported separately so it stays visible.
/// </summary>
public record CycleSummaryDto(
    Guid Id,
    Guid TeamId,
    int Number,
    string? Name,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    CycleStatus Status,
    int TotalIssues,
    int ScopeIssues,
    int CompletedIssues,
    int InProgressIssues,
    int CanceledIssues,
    int ScopeEstimate,
    int CompletedEstimate,
    double ProgressPercent);

// The dates are nullable so that [Required] can actually do its job: on a
// non-nullable DateTimeOffset it is a no-op, and an omitted date would quietly
// bind to 0001-01-01 and be reported as a domain rule violation (422) instead of
// the missing field (400) that it is.
public record CreateCycleRequest(
    [MaxLength(200)] string? Name,
    [Required] DateTimeOffset? StartsAt,
    [Required] DateTimeOffset? EndsAt);

public record UpdateCycleRequest(
    [MaxLength(200)] string? Name,
    [Required] DateTimeOffset? StartsAt,
    [Required] DateTimeOffset? EndsAt);

public static class CycleMappings
{
    public static CycleDto ToDto(this Cycle cycle, DateTimeOffset now) => new(
        cycle.Id,
        cycle.TeamId,
        cycle.Number,
        cycle.Name,
        cycle.StartsAt,
        cycle.EndsAt,
        CycleSchedule.StatusAt(cycle.StartsAt, cycle.EndsAt, now));
}
