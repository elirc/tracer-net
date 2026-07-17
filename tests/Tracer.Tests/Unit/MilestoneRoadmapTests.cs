using Tracer.Domain;
using Tracer.Domain.Entities;

namespace Tracer.Tests.Unit;

public class MilestoneRoadmapTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Future = Now.AddDays(7);
    private static readonly DateTimeOffset Past = Now.AddDays(-7);

    private static readonly WorkflowStateType Backlog = WorkflowStateType.Backlog;
    private static readonly WorkflowStateType InProgress = WorkflowStateType.InProgress;
    private static readonly WorkflowStateType Done = WorkflowStateType.Done;
    private static readonly WorkflowStateType Canceled = WorkflowStateType.Canceled;

    [Fact]
    public void An_empty_milestone_ahead_of_its_date_is_upcoming()
    {
        var p = MilestoneRoadmap.Evaluate([], Future, Now);

        Assert.Equal(MilestoneStatus.Upcoming, p.Status);
        Assert.Equal(0, p.ProgressPercent);
        Assert.Equal(0, p.ScopeIssues);
    }

    [Fact]
    public void An_empty_milestone_past_its_date_is_overdue_not_complete()
    {
        // Nothing was delivered, so this is not "100% of zero" — it is overdue.
        var p = MilestoneRoadmap.Evaluate([], Past, Now);

        Assert.Equal(MilestoneStatus.Overdue, p.Status);
        Assert.Equal(0, p.ProgressPercent);
    }

    [Fact]
    public void Progress_is_completed_over_scope()
    {
        var p = MilestoneRoadmap.Evaluate([Done, Done, InProgress, Backlog], Future, Now);

        Assert.Equal(4, p.TotalIssues);
        Assert.Equal(4, p.ScopeIssues);
        Assert.Equal(2, p.CompletedIssues);
        Assert.Equal(50, p.ProgressPercent);
        Assert.Equal(MilestoneStatus.Upcoming, p.Status);
    }

    [Fact]
    public void Canceled_issues_are_dropped_from_the_denominator()
    {
        // 1 done of 2 in scope = 50%; the canceled issue does not drag it to 33%.
        var p = MilestoneRoadmap.Evaluate([Done, InProgress, Canceled], Future, Now);

        Assert.Equal(3, p.TotalIssues);
        Assert.Equal(2, p.ScopeIssues);
        Assert.Equal(1, p.CompletedIssues);
        Assert.Equal(50, p.ProgressPercent);
    }

    [Fact]
    public void Every_issue_done_is_complete_even_past_the_target()
    {
        var p = MilestoneRoadmap.Evaluate([Done, Done], Past, Now);

        // It shipped. Late, but shipped.
        Assert.Equal(MilestoneStatus.Completed, p.Status);
        Assert.Equal(100, p.ProgressPercent);
    }

    [Fact]
    public void A_milestone_of_only_canceled_work_is_overdue_when_late()
    {
        var p = MilestoneRoadmap.Evaluate([Canceled, Canceled], Past, Now);

        Assert.Equal(0, p.ScopeIssues);
        Assert.Equal(MilestoneStatus.Overdue, p.Status);
    }

    [Fact]
    public void The_target_instant_itself_counts_as_reached()
    {
        // now == target, work outstanding: on-or-past the date is overdue.
        var p = MilestoneRoadmap.Evaluate([InProgress], Now, Now);

        Assert.Equal(MilestoneStatus.Overdue, p.Status);
    }
}
