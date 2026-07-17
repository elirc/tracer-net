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
    string? Assignee,
    Guid StateId,
    string State,
    Guid? ProjectId,
    Guid? CycleId,
    Guid? ParentId,
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
    Guid? ProjectId = null,
    Guid? CycleId = null,
    [MaxLength(100)] string? Assignee = null,
    Guid? ParentId = null);

// ParentId follows ProjectId and CycleId: this is a PUT, so omitting it means
// "no parent" and un-nests the issue. Sending it means "this parent".
public record UpdateIssueRequest(
    [Required, MaxLength(500)] string Title,
    string? Description,
    IssuePriority Priority,
    [Range(0, 100)] int? Estimate,
    Guid? ProjectId,
    Guid? CycleId = null,
    [MaxLength(100)] string? Assignee = null,
    Guid? ParentId = null);

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
        issue.Assignee,
        issue.StateId,
        stateName,
        issue.ProjectId,
        issue.CycleId,
        issue.ParentId,
        issue.Position,
        issue.Labels.Select(l => new LabelDto(l.Id, l.Name, l.Color)).ToList(),
        issue.CreatedAt,
        issue.UpdatedAt);
}
