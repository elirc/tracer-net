using Tracer.Domain.Entities;

namespace Tracer.Api.Contracts;

public record WorkflowStateDto(Guid Id, Guid TeamId, string Name, WorkflowStateType Type, int Position, string Color);

public static class WorkflowStateMappings
{
    public static WorkflowStateDto ToDto(this WorkflowState state) =>
        new(state.Id, state.TeamId, state.Name, state.Type, state.Position, state.Color);
}
