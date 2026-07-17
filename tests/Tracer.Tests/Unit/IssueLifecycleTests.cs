using Tracer.Domain;
using Tracer.Domain.Entities;

namespace Tracer.Tests.Unit;

public class IssueLifecycleTests
{
    private static readonly DateTimeOffset Created = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    private static StateTransition To(WorkflowStateType type, int hoursAfterCreate) =>
        new(type, Created.AddHours(hoursAfterCreate));

    [Fact]
    public void A_straightforward_run_measures_start_to_completion()
    {
        var life = IssueLifecycles.Reconstruct(Created, WorkflowStateType.Done,
        [
            To(WorkflowStateType.InProgress, 5),
            To(WorkflowStateType.Done, 20),
        ]);

        Assert.Equal(Created.AddHours(5), life.StartedAt);
        Assert.Equal(Created.AddHours(20), life.CompletedAt);
        Assert.Equal(TimeSpan.FromHours(15), life.CycleTime);
    }

    [Fact]
    public void An_issue_not_currently_done_has_no_completion()
    {
        // It was Done once, then reopened: current state is what counts.
        var life = IssueLifecycles.Reconstruct(Created, WorkflowStateType.InProgress,
        [
            To(WorkflowStateType.InProgress, 2),
            To(WorkflowStateType.Done, 10),
            To(WorkflowStateType.InProgress, 12),
        ]);

        Assert.Null(life.CompletedAt);
        Assert.Null(life.CycleTime);
        Assert.Equal(Created.AddHours(2), life.StartedAt);
    }

    [Fact]
    public void Completion_is_the_last_move_into_done_and_start_is_the_first_into_progress()
    {
        // Bounced: started, done, reopened, done again. Measured end to end.
        var life = IssueLifecycles.Reconstruct(Created, WorkflowStateType.Done,
        [
            To(WorkflowStateType.InProgress, 3),
            To(WorkflowStateType.Done, 8),
            To(WorkflowStateType.InProgress, 10),
            To(WorkflowStateType.Done, 30),
        ]);

        Assert.Equal(Created.AddHours(3), life.StartedAt);
        Assert.Equal(Created.AddHours(30), life.CompletedAt);
        Assert.Equal(TimeSpan.FromHours(27), life.CycleTime);
    }

    [Fact]
    public void A_done_issue_with_no_recorded_history_falls_back_to_its_creation()
    {
        // Created straight into Done, or history older than the activity log: the
        // honest reading is a lifecycle spanning its creation instant, not a gap.
        var life = IssueLifecycles.Reconstruct(Created, WorkflowStateType.Done, []);

        Assert.Equal(Created, life.StartedAt);
        Assert.Equal(Created, life.CompletedAt);
        Assert.Equal(TimeSpan.Zero, life.CycleTime);
    }

    [Fact]
    public void A_done_issue_never_seen_to_start_uses_creation_as_the_start()
    {
        var life = IssueLifecycles.Reconstruct(Created, WorkflowStateType.Done,
        [
            To(WorkflowStateType.Done, 12),
        ]);

        Assert.Equal(Created, life.StartedAt);
        Assert.Equal(Created.AddHours(12), life.CompletedAt);
        Assert.Equal(TimeSpan.FromHours(12), life.CycleTime);
    }

    [Fact]
    public void Transitions_out_of_order_are_sorted_before_reading()
    {
        var life = IssueLifecycles.Reconstruct(Created, WorkflowStateType.Done,
        [
            To(WorkflowStateType.Done, 20),
            To(WorkflowStateType.InProgress, 4),
        ]);

        Assert.Equal(Created.AddHours(4), life.StartedAt);
        Assert.Equal(Created.AddHours(20), life.CompletedAt);
    }

    [Fact]
    public void A_backlog_issue_has_neither_instant()
    {
        var life = IssueLifecycles.Reconstruct(Created, WorkflowStateType.Backlog, []);

        Assert.Null(life.StartedAt);
        Assert.Null(life.CompletedAt);
        Assert.Null(life.CycleTime);
    }
}
