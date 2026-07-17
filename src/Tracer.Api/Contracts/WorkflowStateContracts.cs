using System.ComponentModel.DataAnnotations;
using Tracer.Domain.Entities;

namespace Tracer.Api.Contracts;

public record WorkflowStateDto(Guid Id, Guid TeamId, string Name, WorkflowStateType Type, int Position, string Color);

public record CreateWorkflowStateRequest(
    [Required, MaxLength(100)] string Name,
    WorkflowStateType Type,
    [MaxLength(9), RegularExpression("^#[0-9a-fA-F]{6}$", ErrorMessage = "Color must be a hex color like #5e6ad2.")]
    string? Color = null,
    [Range(0, int.MaxValue)] int? Position = null);

public record UpdateWorkflowStateRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(9), RegularExpression("^#[0-9a-fA-F]{6}$", ErrorMessage = "Color must be a hex color like #5e6ad2.")]
    string Color,
    [Range(0, int.MaxValue)] int Position);

public static class WorkflowStateMappings
{
    public static WorkflowStateDto ToDto(this WorkflowState state) =>
        new(state.Id, state.TeamId, state.Name, state.Type, state.Position, state.Color);
}
