using Tracer.Domain.Entities;

namespace Tracer.Domain;

/// <summary>When an issue started and finished, reconstructed from its history.</summary>
public readonly record struct IssueLifecycle(DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt)
{
    /// <summary>
    /// Time from first pickup to final completion, or null when the issue is not
    /// finished or the two instants are out of order.
    /// </summary>
    public TimeSpan? CycleTime =>
        StartedAt is { } started && CompletedAt is { } completed && completed >= started
            ? completed - started
            : null;
}

/// <summary>One move into a workflow state, with when it happened.</summary>
public readonly record struct StateTransition(WorkflowStateType To, DateTimeOffset At);

/// <summary>
/// Derives an issue's start and completion instants from its recorded state
/// transitions — the raw material behind throughput and cycle-time.
///
/// <para>
/// Two rules keep it honest against a messy history:
/// </para>
/// <list type="bullet">
/// <item>
/// An issue counts as <b>completed</b> only if it is <i>currently</i> in a Done
/// state. A finished-then-reopened issue is work in progress again, and counting
/// its old completion would report work as shipped that the board says is not.
/// </item>
/// <item>
/// When an instant cannot be read from the transitions — an issue created
/// straight into Done, or history older than the activity log — it falls back to
/// the issue's creation time rather than being dropped. A cycle time spanning the
/// whole life of such an issue is a defensible reading; a silently missing one is
/// not.
/// </item>
/// </list>
///
/// <para>
/// "Started" is the <i>first</i> move into In Progress and "completed" is the
/// <i>last</i> move into Done, so an issue that bounced back and forth is measured
/// end to end rather than only across its final lap.
/// </para>
/// </summary>
public static class IssueLifecycles
{
    public static IssueLifecycle Reconstruct(
        DateTimeOffset createdAt,
        WorkflowStateType currentType,
        IEnumerable<StateTransition> transitions)
    {
        var ordered = transitions.OrderBy(t => t.At).ToList();

        DateTimeOffset? startedAt = ordered
            .Where(t => t.To == WorkflowStateType.InProgress)
            .Select(t => (DateTimeOffset?)t.At)
            .FirstOrDefault();

        DateTimeOffset? completedAt = null;
        if (currentType == WorkflowStateType.Done)
        {
            completedAt = ordered
                .Where(t => t.To == WorkflowStateType.Done)
                .Select(t => (DateTimeOffset?)t.At)
                .LastOrDefault() ?? createdAt;

            // Finished but never seen to start: the honest start is its creation.
            startedAt ??= createdAt;
        }

        return new IssueLifecycle(startedAt, completedAt);
    }
}
