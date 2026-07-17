using System.ComponentModel.DataAnnotations;

namespace Tracer.Api.Contracts;

public record TeamDto(Guid Id, string Name, string Key, DateTimeOffset CreatedAt);

public record CreateTeamRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(10), RegularExpression("^[A-Z][A-Z0-9]*$",
        ErrorMessage = "Key must be uppercase letters/digits starting with a letter, e.g. ENG.")]
    string Key);

public record UpdateTeamRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(10), RegularExpression("^[A-Z][A-Z0-9]*$",
        ErrorMessage = "Key must be uppercase letters/digits starting with a letter, e.g. ENG.")]
    string Key);
