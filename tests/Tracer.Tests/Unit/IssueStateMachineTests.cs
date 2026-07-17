using Tracer.Domain;
using Tracer.Domain.Entities;

namespace Tracer.Tests.Unit;

public class IssueStateMachineTests
{
    [Theory]
    [InlineData(WorkflowStateType.Backlog, WorkflowStateType.Todo)]
    [InlineData(WorkflowStateType.Backlog, WorkflowStateType.InProgress)]
    [InlineData(WorkflowStateType.Backlog, WorkflowStateType.Canceled)]
    [InlineData(WorkflowStateType.Todo, WorkflowStateType.Backlog)]
    [InlineData(WorkflowStateType.Todo, WorkflowStateType.InProgress)]
    [InlineData(WorkflowStateType.Todo, WorkflowStateType.Canceled)]
    [InlineData(WorkflowStateType.InProgress, WorkflowStateType.Todo)]
    [InlineData(WorkflowStateType.InProgress, WorkflowStateType.Done)]
    [InlineData(WorkflowStateType.InProgress, WorkflowStateType.Canceled)]
    [InlineData(WorkflowStateType.Done, WorkflowStateType.InProgress)]
    [InlineData(WorkflowStateType.Done, WorkflowStateType.Todo)]
    [InlineData(WorkflowStateType.Canceled, WorkflowStateType.Backlog)]
    [InlineData(WorkflowStateType.Canceled, WorkflowStateType.Todo)]
    public void Allows_valid_transitions(WorkflowStateType from, WorkflowStateType to) =>
        Assert.True(IssueStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(WorkflowStateType.Backlog, WorkflowStateType.Done)]
    [InlineData(WorkflowStateType.Todo, WorkflowStateType.Done)]
    [InlineData(WorkflowStateType.InProgress, WorkflowStateType.Backlog)]
    [InlineData(WorkflowStateType.Done, WorkflowStateType.Backlog)]
    [InlineData(WorkflowStateType.Done, WorkflowStateType.Canceled)]
    [InlineData(WorkflowStateType.Canceled, WorkflowStateType.InProgress)]
    [InlineData(WorkflowStateType.Canceled, WorkflowStateType.Done)]
    public void Rejects_invalid_transitions(WorkflowStateType from, WorkflowStateType to) =>
        Assert.False(IssueStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(WorkflowStateType.Backlog)]
    [InlineData(WorkflowStateType.Todo)]
    [InlineData(WorkflowStateType.InProgress)]
    [InlineData(WorkflowStateType.Done)]
    [InlineData(WorkflowStateType.Canceled)]
    public void Same_category_moves_are_always_allowed(WorkflowStateType type) =>
        Assert.True(IssueStateMachine.CanTransition(type, type));
}
