using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// The caller's own identity. Any authenticated user may read this — it is how a
/// client discovers which teams its key actually reaches, rather than probing
/// team ids and reading the 404s.
///
/// No <c>[Authorize]</c> attribute is needed: the fallback policy in
/// <c>Program.cs</c> already requires an authenticated user everywhere.
/// </summary>
[ApiController]
[Route("api/me")]
public class MeController(TracerDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<MeDto>> Get()
    {
        var user = await db.Users.FindAsync(User.UserId());
        if (user is null)
        {
            // The key authenticated, so this user existed moments ago and has
            // since been deleted. The credential is dead, not the request bad.
            return Problem(
                title: "Account no longer exists.",
                detail: "The user this API key belongs to has been deleted.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var teams = await VisibleTeams(user)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new TeamDto(t.Id, t.Name, t.Key, t.CreatedAt))
            .ToListAsync();

        return Ok(new MeDto(user.Id, user.Handle, user.Name, user.Role, teams));
    }

    /// <summary>
    /// An admin's reach is not stored as membership rows, so "my teams" means
    /// every team for them and the joined teams for everyone else.
    /// </summary>
    private IQueryable<Team> VisibleTeams(User user) =>
        user.Role == WorkspaceRole.Admin
            ? db.Teams
            : db.Teams.Where(t => t.Memberships.Any(m => m.UserId == user.Id));
}
