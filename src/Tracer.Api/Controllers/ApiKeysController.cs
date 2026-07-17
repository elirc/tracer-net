using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// API key lifecycle.
///
/// Not admin-only, but not open either: a user manages their own keys, and an
/// admin manages anyone's. That rule is one method — <see cref="MayManage"/> —
/// rather than a role attribute, because "yours" is a property of the row being
/// addressed, which a role check cannot see.
///
/// <para>
/// <b>Why some denials are 403 and others 404.</b> The rule is that a status code
/// must never depend on whether a thing the caller cannot see exists. The
/// <c>/users/{userId}/api-keys</c> routes check <see cref="MayManage"/> *before*
/// looking the user up, so a member gets the same 403 for any id but their own,
/// real or not — nothing leaks, and 403 is the honest answer. Addressing a key by
/// its own id is different: a 403 there would only be reachable for a key that
/// exists, turning the status code into an existence oracle for opaque ids. Those
/// routes answer 404 for both.
/// </para>
/// </summary>
[ApiController]
public class ApiKeysController(TracerDbContext db) : ControllerBase
{
    [HttpGet("api/users/{userId:guid}/api-keys")]
    public async Task<ActionResult<PagedResult<ApiKeyDto>>> ListForUser(Guid userId, [FromQuery] PageQuery paging)
    {
        if (!MayManage(userId))
        {
            return ForbidManaging();
        }

        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            return this.NotFoundProblem("User", userId);
        }

        var keys = await db.ApiKeys
            .Where(k => k.UserId == userId)
            .OrderBy(k => k.CreatedAt)
            .ThenBy(k => k.Id)
            .Select(k => new ApiKeyDto(k.Id, k.UserId, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt))
            .ToPagedResultAsync(paging);
        return Ok(keys);
    }

    /// <summary>
    /// Mints a key. The response is the only time the raw token exists outside
    /// the caller's own machine: only its hash is stored, so a lost token is
    /// replaced, never recovered.
    /// </summary>
    [HttpPost("api/users/{userId:guid}/api-keys")]
    public async Task<ActionResult<CreatedApiKeyDto>> Create(Guid userId, CreateApiKeyRequest request)
    {
        if (!MayManage(userId))
        {
            return ForbidManaging();
        }

        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            return this.NotFoundProblem("User", userId);
        }

        var rawToken = ApiKeyToken.Mint();
        var key = new ApiKey
        {
            UserId = userId,
            Name = request.Name,
            KeyHash = ApiKeyToken.Hash(rawToken),
            Prefix = ApiKeyToken.PrefixOf(rawToken),
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync();

        return CreatedAtAction(
            nameof(Get),
            new { id = key.Id },
            new CreatedApiKeyDto(key.Id, key.UserId, key.Name, key.Prefix, rawToken, key.CreatedAt));
    }

    [HttpGet("api/api-keys/{id:guid}")]
    public async Task<ActionResult<ApiKeyDto>> Get(Guid id)
    {
        var key = await db.ApiKeys.FindAsync(id);
        if (key is null || !MayManage(key.UserId))
        {
            // Same answer for "no such key" and "not your key": a 403 here would
            // confirm that a key id is real to someone who cannot see it.
            return this.NotFoundProblem("API key", id);
        }

        return Ok(key.ToDto());
    }

    /// <summary>
    /// Revokes a key. The row stays: a deleted key leaves no trace of what was
    /// authenticating with it, which is exactly the question asked after a leak.
    /// Revoking twice is not an error.
    /// </summary>
    [HttpDelete("api/api-keys/{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var key = await db.ApiKeys.FindAsync(id);
        if (key is null || !MayManage(key.UserId))
        {
            return this.NotFoundProblem("API key", id);
        }

        if (key.RevokedAt is null)
        {
            key.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        return NoContent();
    }

    private bool MayManage(Guid userId) => User.IsAdmin() || User.UserId() == userId;

    private ObjectResult ForbidManaging() =>
        Problem(
            title: "Not allowed to manage this user's API keys.",
            detail: "You can manage your own API keys; managing another user's requires the Admin role.",
            statusCode: StatusCodes.Status403Forbidden);
}
