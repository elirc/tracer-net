using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// Teams.
///
/// Creating, renaming, and deleting a team are workspace-shaped decisions, so
/// they are admin-only. Everything a team *contains* — its workflow, labels,
/// projects, cycles, issues — is configured by its own members: a workspace
/// where only admins can add a label is a workspace where admins are a ticket
/// queue.
/// </summary>
[ApiController]
[Route("api/teams")]
public class TeamsController(TracerDbContext db, TeamAccess access) : ControllerBase
{
    /// <summary>
    /// Lists the teams the caller can see: every team for an admin, joined teams
    /// for a member. A member who is on no teams gets an empty list, not a 403 —
    /// nothing was denied them, there is simply nothing there.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<TeamDto>>> List([FromQuery] PageQuery paging)
    {
        var teams = db.Teams.AsQueryable();
        if (!User.IsAdmin())
        {
            var mine = await access.MemberTeamIdsAsync(User);
            teams = teams.Where(t => mine.Contains(t.Id));
        }

        var results = await teams
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Select(t => new TeamDto(t.Id, t.Name, t.Key, t.CreatedAt))
            .ToPagedResultAsync(paging);
        return Ok(results);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TeamDto>> Get(Guid id)
    {
        if (!await access.CanAccessTeamAsync(User, id))
        {
            return this.NotFoundProblem("Team", id);
        }

        var team = await db.Teams.FindAsync(id);
        return Ok(new TeamDto(team!.Id, team.Name, team.Key, team.CreatedAt));
    }

    /// <summary>The roster of a team the caller is on.</summary>
    [HttpGet("{teamId:guid}/members")]
    public async Task<ActionResult<PagedResult<TeamMemberDto>>> ListMembers(Guid teamId, [FromQuery] PageQuery paging)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var members = await db.TeamMemberships
            .Where(m => m.TeamId == teamId)
            .OrderBy(m => m.User!.Handle)
            .ThenBy(m => m.UserId)
            .Select(m => new TeamMemberDto(m.UserId, m.User!.Handle, m.User.Name, m.User.Role, m.CreatedAt))
            .ToPagedResultAsync(paging);
        return Ok(members);
    }

    [HttpPost]
    [Authorize(Roles = nameof(WorkspaceRole.Admin))]
    public async Task<ActionResult<TeamDto>> Create(CreateTeamRequest request)
    {
        if (await db.Teams.AnyAsync(t => t.Key == request.Key))
        {
            return this.ConflictProblem(
                "Team key already in use.",
                $"A team with key '{request.Key}' already exists.");
        }

        var team = new Team { Name = request.Name, Key = request.Key };
        db.Teams.Add(team);
        db.WorkflowStates.AddRange(DefaultWorkflow.CreateStates(team.Id));
        await db.SaveChangesAsync();

        var dto = new TeamDto(team.Id, team.Name, team.Key, team.CreatedAt);
        return CreatedAtAction(nameof(Get), new { id = team.Id }, dto);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = nameof(WorkspaceRole.Admin))]
    public async Task<ActionResult<TeamDto>> Update(Guid id, UpdateTeamRequest request)
    {
        var team = await db.Teams.FindAsync(id);
        if (team is null)
        {
            return this.NotFoundProblem("Team", id);
        }

        if (await db.Teams.AnyAsync(t => t.Key == request.Key && t.Id != id))
        {
            return this.ConflictProblem(
                "Team key already in use.",
                $"A team with key '{request.Key}' already exists.");
        }

        team.Name = request.Name;
        team.Key = request.Key;
        await db.SaveChangesAsync();

        return Ok(new TeamDto(team.Id, team.Name, team.Key, team.CreatedAt));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = nameof(WorkspaceRole.Admin))]
    public async Task<IActionResult> Delete(Guid id)
    {
        var team = await db.Teams.FindAsync(id);
        if (team is null)
        {
            return this.NotFoundProblem("Team", id);
        }

        db.Teams.Remove(team);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
