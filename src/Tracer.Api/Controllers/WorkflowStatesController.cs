using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

[ApiController]
public class WorkflowStatesController(TracerDbContext db, TeamAccess access) : ControllerBase
{
    /// <summary>
    /// A team's workflow, in order. Deliberately not paged: a team's states are its
    /// board columns, a small fixed set (five by default) that the board renders in
    /// full, not a collection that grows with usage. The read is bounded by the
    /// workflow itself, so there is nothing to page.
    /// </summary>
    [HttpGet("api/teams/{teamId:guid}/states")]
    public async Task<ActionResult<List<WorkflowStateDto>>> ListForTeam(Guid teamId)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var states = await db.WorkflowStates
            .Where(s => s.TeamId == teamId)
            .OrderBy(s => s.Position)
            .ToListAsync();

        return Ok(states.Select(s => s.ToDto()).ToList());
    }

    [HttpPost("api/teams/{teamId:guid}/states")]
    public async Task<ActionResult<WorkflowStateDto>> Create(Guid teamId, CreateWorkflowStateRequest request)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        if (await db.WorkflowStates.AnyAsync(s => s.TeamId == teamId && s.Name == request.Name))
        {
            return this.ConflictProblem(
                "State name already in use.",
                $"Team already has a state named '{request.Name}'.");
        }

        var siblings = await db.WorkflowStates
            .Where(s => s.TeamId == teamId)
            .OrderBy(s => s.Position)
            .ToListAsync();

        var position = Math.Clamp(request.Position ?? siblings.Count, 0, siblings.Count);
        foreach (var sibling in siblings.Where(s => s.Position >= position))
        {
            sibling.Position++;
        }

        var state = new WorkflowState
        {
            TeamId = teamId,
            Name = request.Name,
            Type = request.Type,
            Position = position,
            Color = request.Color ?? "#95a2b3",
        };
        db.WorkflowStates.Add(state);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = state.Id }, state.ToDto());
    }

    [HttpGet("api/states/{id:guid}")]
    public async Task<ActionResult<WorkflowStateDto>> GetById(Guid id)
    {
        var state = await db.WorkflowStates.FindAsync(id);
        if (state is null || !await access.CanAccessTeamAsync(User, state.TeamId))
        {
            return this.NotFoundProblem("Workflow state", id);
        }

        return Ok(state.ToDto());
    }

    [HttpPut("api/states/{id:guid}")]
    public async Task<ActionResult<WorkflowStateDto>> Update(Guid id, UpdateWorkflowStateRequest request)
    {
        var state = await db.WorkflowStates.FindAsync(id);
        if (state is null || !await access.CanAccessTeamAsync(User, state.TeamId))
        {
            return this.NotFoundProblem("Workflow state", id);
        }

        if (await db.WorkflowStates.AnyAsync(s => s.TeamId == state.TeamId && s.Name == request.Name && s.Id != id))
        {
            return this.ConflictProblem(
                "State name already in use.",
                $"Team already has a state named '{request.Name}'.");
        }

        var siblings = await db.WorkflowStates
            .Where(s => s.TeamId == state.TeamId)
            .OrderBy(s => s.Position)
            .ToListAsync();

        // Reorder with remove-then-insert semantics, then renumber 0..n-1.
        var target = Math.Clamp(request.Position, 0, siblings.Count - 1);
        siblings.Remove(siblings.Single(s => s.Id == id));
        siblings.Insert(target, state);
        for (var i = 0; i < siblings.Count; i++)
        {
            siblings[i].Position = i;
        }

        state.Name = request.Name;
        state.Color = request.Color;
        await db.SaveChangesAsync();

        return Ok(state.ToDto());
    }

    [HttpDelete("api/states/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var state = await db.WorkflowStates.FindAsync(id);
        if (state is null || !await access.CanAccessTeamAsync(User, state.TeamId))
        {
            return this.NotFoundProblem("Workflow state", id);
        }

        if (await db.Issues.AnyAsync(i => i.StateId == id))
        {
            return this.ConflictProblem(
                "State has issues.",
                "Move or delete the issues in this state before deleting it.");
        }

        if (await db.WorkflowStates.CountAsync(s => s.TeamId == state.TeamId) == 1)
        {
            return this.ConflictProblem(
                "Cannot delete the team's last workflow state.",
                "A team must keep at least one workflow state for its issues to live in.");
        }

        db.WorkflowStates.Remove(state);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
