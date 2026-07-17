using System.ComponentModel.DataAnnotations;
using Tracer.Domain;

namespace Tracer.Api.Contracts;

/// <summary>
/// A milestone on the roadmap, with its progress rolled up. The progress fields
/// are derived on every read from the issues currently pointing at it, never
/// stored, so they cannot drift from the board.
/// </summary>
public record MilestoneDto(
    Guid Id,
    Guid TeamId,
    Guid ProjectId,
    string Name,
    string? Description,
    DateTimeOffset TargetDate,
    DateTimeOffset CreatedAt,
    int TotalIssues,
    int ScopeIssues,
    int CompletedIssues,
    double ProgressPercent,
    MilestoneStatus Status);

// TargetDate is nullable so [Required] can do its job: on a non-nullable
// DateTimeOffset the attribute is a no-op, and an omitted date would bind to
// 0001-01-01 and sail through as a valid-looking target instead of the 400 a
// missing required field should be.
public record CreateMilestoneRequest(
    [Required, MaxLength(200)] string Name,
    string? Description,
    [Required] DateTimeOffset? TargetDate);

public record UpdateMilestoneRequest(
    [Required, MaxLength(200)] string Name,
    string? Description,
    [Required] DateTimeOffset? TargetDate);
