using Tracer.Domain.Entities;

namespace Tracer.Infrastructure;

public static class DefaultWorkflow
{
    /// <summary>
    /// The default, ordered workflow every new team starts with.
    /// Teams can rename, recolor, reorder, and add states afterwards.
    /// </summary>
    public static List<WorkflowState> CreateStates(Guid teamId) =>
    [
        new() { TeamId = teamId, Name = "Backlog", Type = WorkflowStateType.Backlog, Position = 0, Color = "#95a2b3" },
        new() { TeamId = teamId, Name = "Todo", Type = WorkflowStateType.Todo, Position = 1, Color = "#e2e2e2" },
        new() { TeamId = teamId, Name = "In Progress", Type = WorkflowStateType.InProgress, Position = 2, Color = "#f2c94c" },
        new() { TeamId = teamId, Name = "Done", Type = WorkflowStateType.Done, Position = 3, Color = "#5e6ad2" },
        new() { TeamId = teamId, Name = "Canceled", Type = WorkflowStateType.Canceled, Position = 4, Color = "#95a2b3" },
    ];
}
