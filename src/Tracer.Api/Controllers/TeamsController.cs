using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

[ApiController]
[Route("api/teams")]
public class TeamsController(TracerDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<TeamDto>>> List()
    {
        var teams = await db.Teams
            .OrderBy(t => t.CreatedAt)
            .Select(t => new TeamDto(t.Id, t.Name, t.Key, t.CreatedAt))
            .ToListAsync();
        return Ok(teams);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TeamDto>> Get(Guid id)
    {
        var team = await db.Teams.FindAsync(id);
        if (team is null)
        {
            return this.NotFoundProblem("Team", id);
        }

        return Ok(new TeamDto(team.Id, team.Name, team.Key, team.CreatedAt));
    }

    [HttpPost]
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
