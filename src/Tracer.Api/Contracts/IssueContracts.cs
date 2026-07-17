using System.ComponentModel.DataAnnotations;
using Tracer.Domain.Entities;

namespace Tracer.Api.Contracts;

public record LabelDto(Guid Id, string Name, string Color);

public record IssueDto(
    Guid Id,
    Guid TeamId,
    string Identifier,
    int Number,
    string Title,
    string? Description,
    IssuePriority Priority,
    int? Estimate,
    Guid StateId,
    string State,
    Guid? ProjectId,
    Guid? CycleId,
    double Position,
    IReadOnlyList<LabelDto> Labels,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record CreateIssueRequest(
    [Required, MaxLength(500)] string Title,
    string? Description,
    IssuePriority Priority = IssuePriority.None,
    [Range(0, 100)] int? Estimate = null,
    Guid? StateId = null,
    Guid? ProjectId = null);

public record UpdateIssueRequest(
    [Required, MaxLength(500)] string Title,
    string? Description,
    IssuePriority Priority,
    [Range(0, 100)] int? Estimate,
    Guid? ProjectId);

public static class IssueMappings
{
    public static IssueDto ToDto(this Issue issue, string teamKey, string stateName) => new(
        issue.Id,
        issue.TeamId,
        $"{teamKey}-{issue.Number}",
        issue.Number,
        issue.Title,
        issue.Description,
        issue.Priority,
        issue.Estimate,
        issue.StateId,
        stateName,
        issue.ProjectId,
        issue.CycleId,
        issue.Position,
        issue.Labels.Select(l => new LabelDto(l.Id, l.Name, l.Color)).ToList(),
        issue.CreatedAt,
        issue.UpdatedAt);
}
