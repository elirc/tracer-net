using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// User and membership administration — all of it admin-only.
///
/// The role requirement sits on the class rather than on each action because
/// <c>[Authorize]</c> attributes are *additive*: a method-level <c>[Authorize]</c>
/// underneath a class-level <c>[Authorize(Roles = "Admin")]</c> does not relax the
/// role, it adds a second requirement on top of it. Anything a member is allowed
/// to call therefore has to live outside this controller, not be annotated its
/// way out — which is why <c>GET /api/me</c> and the team roster are elsewhere.
/// </summary>
[ApiController]
[Authorize(Roles = nameof(WorkspaceRole.Admin))]
public class UsersController(TracerDbContext db) : ControllerBase
{
    [HttpGet("api/users")]
    public async Task<ActionResult<PagedResult<UserDto>>> List([FromQuery] PageQuery paging)
    {
        var users = await db.Users
            .OrderBy(u => u.Handle)
            .ThenBy(u => u.Id)
            .Select(u => new UserDto(u.Id, u.Handle, u.Name, u.Role, u.CreatedAt))
            .ToPagedResultAsync(paging);
        return Ok(users);
    }

    [HttpGet("api/users/{id:guid}")]
    public async Task<ActionResult<UserDto>> Get(Guid id)
    {
        var user = await db.Users.FindAsync(id);
        return user is null ? this.NotFoundProblem("User", id) : Ok(user.ToDto());
    }

    [HttpPost("api/users")]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request)
    {
        if (await db.Users.AnyAsync(u => u.Handle == request.Handle))
        {
            return this.ConflictProblem(
                "Handle already in use.",
                $"A user with handle '{request.Handle}' already exists.");
        }

        var user = new User { Handle = request.Handle, Name = request.Name, Role = request.Role };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = user.Id }, user.ToDto());
    }

    /// <summary>
    /// Renames a user or changes their role. The handle is immutable: it is
    /// stamped into every <c>Assignee</c> and <c>Author</c> string ever written,
    /// and none of those are foreign keys, so renaming it would silently orphan
    /// history rather than update it.
    /// </summary>
    [HttpPut("api/users/{id:guid}")]
    public async Task<ActionResult<UserDto>> Update(Guid id, UpdateUserRequest request)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null)
        {
            return this.NotFoundProblem("User", id);
        }

        if (user.Role == WorkspaceRole.Admin
            && request.Role != WorkspaceRole.Admin
            && await IsLastAdminAsync(id))
        {
            return LastAdminConflict("demote");
        }

        user.Name = request.Name;
        user.Role = request.Role;
        await db.SaveChangesAsync();

        return Ok(user.ToDto());
    }

    [HttpDelete("api/users/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null)
        {
            return this.NotFoundProblem("User", id);
        }

        if (user.Role == WorkspaceRole.Admin && await IsLastAdminAsync(id))
        {
            return LastAdminConflict("delete");
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Puts a user on a team. Idempotent: adding twice is not an error.</summary>
    [HttpPut("api/users/{userId:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> AddToTeam(Guid userId, Guid teamId)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            return this.NotFoundProblem("User", userId);
        }

        if (!await db.Teams.AnyAsync(t => t.Id == teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        if (!await db.TeamMemberships.AnyAsync(m => m.UserId == userId && m.TeamId == teamId))
        {
            db.TeamMemberships.Add(new TeamMembership { UserId = userId, TeamId = teamId });
            await db.SaveChangesAsync();
        }

        return NoContent();
    }

    [HttpDelete("api/users/{userId:guid}/teams/{teamId:guid}")]
    public async Task<IActionResult> RemoveFromTeam(Guid userId, Guid teamId)
    {
        var membership = await db.TeamMemberships
            .SingleOrDefaultAsync(m => m.UserId == userId && m.TeamId == teamId);
        if (membership is null)
        {
            return this.NotFoundProblem("Team membership", teamId);
        }

        db.TeamMemberships.Remove(membership);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// A workspace with no admin is unadministrable: nobody can mint keys, add
    /// members, or promote anyone back. Nothing in the schema prevents it, so it
    /// is refused here.
    /// </summary>
    private async Task<bool> IsLastAdminAsync(Guid userId) =>
        !await db.Users.AnyAsync(u => u.Role == WorkspaceRole.Admin && u.Id != userId);

    private ObjectResult LastAdminConflict(string verb) =>
        this.ConflictProblem(
            "Cannot remove the last admin.",
            $"Promote another user to admin before you {verb} this one; a workspace with no admin cannot be administered.");
}
