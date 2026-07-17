using System.ComponentModel.DataAnnotations;

namespace Tracer.Api.Contracts;

public record TeamLabelDto(Guid Id, Guid TeamId, string Name, string Color);

public record CreateLabelRequest(
    [Required, MaxLength(100)] string Name,
    [MaxLength(9), RegularExpression("^#[0-9a-fA-F]{6}$", ErrorMessage = "Color must be a hex color like #5e6ad2.")]
    string? Color = null);

public record UpdateLabelRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(9), RegularExpression("^#[0-9a-fA-F]{6}$", ErrorMessage = "Color must be a hex color like #5e6ad2.")]
    string Color);
