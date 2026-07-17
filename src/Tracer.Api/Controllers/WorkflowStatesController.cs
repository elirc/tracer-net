using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Contracts;
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
}
