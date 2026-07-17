using System.Security.Claims;
using Tracer.Domain.Entities;

namespace Tracer.Api.Auth;

/// <summary>Reads the identity that <see cref="ApiKeyAuthenticationHandler"/> put on the request.</summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The caller's user id. Throws when unauthenticated: every route that reads
    /// this sits behind the fallback authorization policy, so an absent id means
    /// the pipeline is misconfigured, not that a user made a bad request. Failing
    /// loudly beats returning <see cref="Guid.Empty"/> and writing rows owned by
    /// nobody.
    /// </summary>
    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("No authenticated user on this request.");

    public static string Handle(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Name)
        ?? throw new InvalidOperationException("No authenticated user on this request.");

    public static bool IsAdmin(this ClaimsPrincipal principal) =>
        principal.IsInRole(nameof(WorkspaceRole.Admin));
}
