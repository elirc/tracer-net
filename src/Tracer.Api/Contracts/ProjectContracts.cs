using System.ComponentModel.DataAnnotations;

namespace Tracer.Api.Contracts;

public record ProjectDto(Guid Id, Guid TeamId, string Name, string? Description, DateTimeOffset CreatedAt);

public record CreateProjectRequest(
    [Required, MaxLength(200)] string Name,
    string? Description);

public record UpdateProjectRequest(
    [Required, MaxLength(200)] string Name,
    string? Description);
