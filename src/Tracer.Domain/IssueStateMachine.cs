using Tracer.Domain.Entities;

namespace Tracer.Domain;

/// <summary>
/// Validates issue state transitions between workflow state categories.
///
/// Rules:
/// - Transitions between states of the same category are always allowed
///   (e.g. moving between two custom "In Progress" states).
/// - Backlog  -> Todo, InProgress, Canceled
/// - Todo     -> Backlog, InProgress, Canceled
/// - InProgress -> Todo, Done, Canceled
/// - Done     -> Todo, InProgress (reopen)
/// - Canceled -> Backlog, Todo (reopen)
///
/// Notably forbidden: skipping straight from Backlog/Todo to Done,
/// canceling an already-Done issue, and resurrecting a Canceled issue
/// directly into InProgress or Done.
/// </summary>
public static class IssueStateMachine
{
    private static readonly Dictionary<WorkflowStateType, WorkflowStateType[]> Allowed = new()
    {
        [WorkflowStateType.Backlog] = [WorkflowStateType.Todo, WorkflowStateType.InProgress, WorkflowStateType.Canceled],
        [WorkflowStateType.Todo] = [WorkflowStateType.Backlog, WorkflowStateType.InProgress, WorkflowStateType.Canceled],
        [WorkflowStateType.InProgress] = [WorkflowStateType.Todo, WorkflowStateType.Done, WorkflowStateType.Canceled],
        [WorkflowStateType.Done] = [WorkflowStateType.Todo, WorkflowStateType.InProgress],
        [WorkflowStateType.Canceled] = [WorkflowStateType.Backlog, WorkflowStateType.Todo],
    };

    public static bool CanTransition(WorkflowStateType from, WorkflowStateType to) =>
        from == to || Allowed[from].Contains(to);
}
