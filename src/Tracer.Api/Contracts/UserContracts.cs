using System.ComponentModel.DataAnnotations;
using Tracer.Domain.Entities;

namespace Tracer.Api.Contracts;

public record UserDto(Guid Id, string Handle, string Name, WorkspaceRole Role, DateTimeOffset CreatedAt);

/// <summary>Who the caller is, and which teams that gets them into.</summary>
public record MeDto(
    Guid Id,
    string Handle,
    string Name,
    WorkspaceRole Role,
    IReadOnlyList<TeamDto> Teams);

public record CreateUserRequest(
    [Required, MaxLength(100), RegularExpression("^[a-z][a-z0-9_.-]*$",
        ErrorMessage = "Handle must be lowercase, start with a letter, e.g. ana.")]
    string Handle,
    [Required, MaxLength(200)] string Name,
    WorkspaceRole Role = WorkspaceRole.Member);

public record UpdateUserRequest(
    [Required, MaxLength(200)] string Name,
    WorkspaceRole Role);

public record TeamMemberDto(Guid UserId, string Handle, string Name, WorkspaceRole Role, DateTimeOffset JoinedAt);

/// <summary>An API key as it can safely be listed: everything except the token itself.</summary>
public record ApiKeyDto(
    Guid Id,
    Guid UserId,
    string Name,
    string Prefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// The one response that carries a raw <see cref="Token"/>. It is returned by
/// creation and never again — only its hash is stored, so a lost token is
/// replaced rather than recovered.
/// </summary>
public record CreatedApiKeyDto(
    Guid Id,
    Guid UserId,
    string Name,
    string Prefix,
    string Token,
    DateTimeOffset CreatedAt);

public record CreateApiKeyRequest([Required, MaxLength(100)] string Name);

public static class UserMappings
{
    public static UserDto ToDto(this User user) => new(user.Id, user.Handle, user.Name, user.Role, user.CreatedAt);

    public static ApiKeyDto ToDto(this ApiKey key) =>
        new(key.Id, key.UserId, key.Name, key.Prefix, key.CreatedAt, key.LastUsedAt, key.RevokedAt);
}
