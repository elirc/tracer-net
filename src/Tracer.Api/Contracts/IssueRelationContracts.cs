using System.ComponentModel.DataAnnotations;
using Tracer.Domain;

namespace Tracer.Api.Contracts;

/// <summary>
/// A relation as the issue you asked about sees it. <see cref="Kind"/> is
/// therefore relative to that issue: the same stored row is <c>Blocks</c> to one
/// end and <c>BlockedBy</c> to the other.
/// </summary>
public record IssueRelationDto(
    Guid Id,
    IssueRelationKind Kind,
    Guid IssueId,
    string Identifier,
    string Title,
    string State,
    DateTimeOffset CreatedAt);

// IssueId is nullable so [Required] can reject an omitted value with a 400. On a
// non-nullable Guid the attribute is a no-op and the field binds to Guid.Empty,
// which would surface as "no such issue" (404) rather than the missing field it is.
public record CreateIssueRelationRequest(
    [Required] IssueRelationKind? Kind,
    [Required] Guid? IssueId);

/// <summary>
/// Progress across an issue's sub-issues.
///
/// Canceled children are dropped from <see cref="ScopeIssues"/> and reported
/// separately, exactly as a cycle's roll-up treats canceled work: calling a
/// sub-task off should not count against the parent's completion, but it should
/// not vanish either. Two roll-ups in one product answering the same question
/// two different ways is worse than either answer.
/// </summary>
public record SubIssueRollupDto(
    int TotalIssues,
    int ScopeIssues,
    int CompletedIssues,
    int InProgressIssues,
    int CanceledIssues,
    int ScopeEstimate,
    int CompletedEstimate,
    double ProgressPercent);

public record SubIssuesDto(SubIssueRollupDto Rollup, IReadOnlyList<IssueDto> Items);
