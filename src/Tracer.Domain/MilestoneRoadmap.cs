using Tracer.Domain.Entities;

namespace Tracer.Domain;

/// <summary>Where a milestone stands, derived from its issues and its target date.</summary>
public enum MilestoneStatus
{
    /// <summary>Still ahead of its target date, or on it, with work outstanding.</summary>
    Upcoming = 0,

    /// <summary>Past its target date and not finished.</summary>
    Overdue = 1,

    /// <summary>Every issue in scope is done. A landed milestone is landed even if it landed late.</summary>
    Completed = 2,
}

/// <summary>A milestone's roll-up: how much of it is done, and where that leaves it.</summary>
public readonly record struct MilestoneProgress(
    int TotalIssues,
    int ScopeIssues,
    int CompletedIssues,
    double ProgressPercent,
    MilestoneStatus Status);

/// <summary>
/// Turns a milestone's issues and its target date into a progress roll-up, using
/// the same scope rule the rest of the product uses: canceled work is dropped from
/// the denominator so calling something off cannot make a milestone look behind.
///
/// <para>
/// Completion beats the calendar: a milestone whose every issue is done is
/// <see cref="MilestoneStatus.Completed"/> even past its target date, because it
/// shipped. An empty milestone past its date is <see cref="MilestoneStatus.Overdue"/>
/// — nothing was delivered — rather than quietly "complete" on a denominator of
/// zero.
/// </para>
/// </summary>
public static class MilestoneRoadmap
{
    public static MilestoneProgress Evaluate(
        IReadOnlyList<WorkflowStateType> issueStateTypes,
        DateTimeOffset targetDate,
        DateTimeOffset now)
    {
        var scope = issueStateTypes.Count(t => t != WorkflowStateType.Canceled);
        var completed = issueStateTypes.Count(t => t == WorkflowStateType.Done);

        var progress = scope == 0 ? 0 : Math.Round(completed * 100.0 / scope, 1);

        var status =
            scope > 0 && completed == scope ? MilestoneStatus.Completed
            : now >= targetDate ? MilestoneStatus.Overdue
            : MilestoneStatus.Upcoming;

        return new MilestoneProgress(issueStateTypes.Count, scope, completed, progress, status);
    }
}
