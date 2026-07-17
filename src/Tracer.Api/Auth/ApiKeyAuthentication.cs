using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tracer.Domain;
using Tracer.Infrastructure;

namespace Tracer.Api.Auth;

/// <summary>
/// Authenticates a request from an <c>X-Api-Key</c> header.
///
/// This is a real <see cref="AuthenticationHandler{TOptions}"/> rather than a
/// hand-rolled middleware check so that the rest of the framework works as
/// documented: <c>[Authorize]</c>, role policies, the fallback policy, and
/// <c>HttpContext.User</c> all behave the way every ASP.NET Core reader already
/// expects. A bespoke middleware that stuffs a user into <c>HttpContext.Items</c>
/// gets none of that and quietly diverges from the framework's 401/403 semantics.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TracerDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    /// <summary>
    /// How stale <see cref="Domain.Entities.ApiKey.LastUsedAt"/> may get. Recording
    /// every request would make a write out of every read; answering "is this key
    /// still in use?" does not need second-level precision.
    /// </summary>
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var header))
        {
            // No credential offered at all is NoResult, not Fail: it lets the
            // authorization layer decide whether this endpoint even needed one,
            // so anonymous endpoints stay anonymous.
            return AuthenticateResult.NoResult();
        }

        var rawToken = header.ToString();
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return AuthenticateResult.Fail("Empty API key.");
        }

        var hash = ApiKeyToken.Hash(rawToken);
        var key = await db.ApiKeys
            .Include(k => k.User)
            .SingleOrDefaultAsync(k => k.KeyHash == hash);

        // One message for "no such key" and for "revoked key". Telling a caller
        // which of the two it was confirms that a token is genuine, which is
        // exactly what someone testing a stolen key wants to know.
        if (key is null || key.RevokedAt is not null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var now = DateTimeOffset.UtcNow;
        if (key.LastUsedAt is null || now - key.LastUsedAt.Value > LastUsedResolution)
        {
            key.LastUsedAt = now;
            await db.SaveChangesAsync();
        }

        var user = key.User!;
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Handle),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
            ],
            SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
