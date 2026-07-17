using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

[ApiController]
public class WorkflowStatesController(TracerDbContext db) : ControllerBase
{
    [HttpGet("api/teams/{teamId:guid}/states")]
    public async Task<ActionResult<List<WorkflowStateDto>>> ListForTeam(Guid teamId)
    {
        if (!await db.Teams.AnyAsync(t => t.Id == teamId))
        {
            return NotFound();
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
        if (!await db.Teams.AnyAsync(t => t.Id == teamId))
        {
            return NotFound();
        }

        if (await db.WorkflowStates.AnyAsync(s => s.TeamId == teamId && s.Name == request.Name))
        {
            return Conflict(new ProblemDetails
            {
                Title = "State name already in use.",
                Detail = $"Team already has a state named '{request.Name}'.",
                Status = StatusCodes.Status409Conflict,
            });
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
        if (state is null)
        {
            return NotFound();
        }

        return Ok(state.ToDto());
    }

    [HttpPut("api/states/{id:guid}")]
    public async Task<ActionResult<WorkflowStateDto>> Update(Guid id, UpdateWorkflowStateRequest request)
    {
        var state = await db.WorkflowStates.FindAsync(id);
        if (state is null)
        {
            return NotFound();
        }

        if (await db.WorkflowStates.AnyAsync(s => s.TeamId == state.TeamId && s.Name == request.Name && s.Id != id))
        {
            return Conflict(new ProblemDetails
            {
                Title = "State name already in use.",
                Detail = $"Team already has a state named '{request.Name}'.",
                Status = StatusCodes.Status409Conflict,
            });
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
        if (state is null)
        {
            return NotFound();
        }

        if (await db.Issues.AnyAsync(i => i.StateId == id))
        {
            return Conflict(new ProblemDetails
            {
                Title = "State has issues.",
                Detail = "Move or delete the issues in this state before deleting it.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        if (await db.WorkflowStates.CountAsync(s => s.TeamId == state.TeamId) == 1)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Cannot delete the team's last workflow state.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        db.WorkflowStates.Remove(state);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
