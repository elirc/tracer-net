using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

[ApiController]
public class LabelsController(TracerDbContext db) : ControllerBase
{
    [HttpGet("api/teams/{teamId:guid}/labels")]
    public async Task<ActionResult<List<TeamLabelDto>>> ListForTeam(Guid teamId)
    {
        if (!await db.Teams.AnyAsync(t => t.Id == teamId))
        {
            return NotFound();
        }

        var labels = await db.Labels
            .Where(l => l.TeamId == teamId)
            .OrderBy(l => l.Name)
            .Select(l => new TeamLabelDto(l.Id, l.TeamId, l.Name, l.Color))
            .ToListAsync();
        return Ok(labels);
    }

    [HttpPost("api/teams/{teamId:guid}/labels")]
    public async Task<ActionResult<TeamLabelDto>> Create(Guid teamId, CreateLabelRequest request)
    {
        if (!await db.Teams.AnyAsync(t => t.Id == teamId))
        {
            return NotFound();
        }

        if (await db.Labels.AnyAsync(l => l.TeamId == teamId && l.Name == request.Name))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Label name already in use.",
                Detail = $"Team already has a label named '{request.Name}'.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        var label = new Label { TeamId = teamId, Name = request.Name, Color = request.Color ?? "#5e6ad2" };
        db.Labels.Add(label);
        await db.SaveChangesAsync();

        var dto = new TeamLabelDto(label.Id, label.TeamId, label.Name, label.Color);
        return CreatedAtAction(nameof(GetById), new { id = label.Id }, dto);
    }

    [HttpGet("api/labels/{id:guid}")]
    public async Task<ActionResult<TeamLabelDto>> GetById(Guid id)
    {
        var label = await db.Labels.FindAsync(id);
        if (label is null)
        {
            return NotFound();
        }

        return Ok(new TeamLabelDto(label.Id, label.TeamId, label.Name, label.Color));
    }

    [HttpPut("api/labels/{id:guid}")]
    public async Task<ActionResult<TeamLabelDto>> Update(Guid id, UpdateLabelRequest request)
    {
        var label = await db.Labels.FindAsync(id);
        if (label is null)
        {
            return NotFound();
        }

        if (await db.Labels.AnyAsync(l => l.TeamId == label.TeamId && l.Name == request.Name && l.Id != id))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Label name already in use.",
                Detail = $"Team already has a label named '{request.Name}'.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        label.Name = request.Name;
        label.Color = request.Color;
        await db.SaveChangesAsync();

        return Ok(new TeamLabelDto(label.Id, label.TeamId, label.Name, label.Color));
    }

    [HttpDelete("api/labels/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var label = await db.Labels.FindAsync(id);
        if (label is null)
        {
            return NotFound();
        }

        db.Labels.Remove(label);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("api/issues/{issueId:guid}/labels/{labelId:guid}")]
    public async Task<IActionResult> Attach(Guid issueId, Guid labelId)
    {
        var issue = await db.Issues.Include(i => i.Labels).SingleOrDefaultAsync(i => i.Id == issueId);
        if (issue is null)
        {
            return NotFound();
        }

        var label = await db.Labels.FindAsync(labelId);
        if (label is null)
        {
            return NotFound();
        }

        if (label.TeamId != issue.TeamId)
        {
            return ValidationProblem(title: "Label belongs to a different team.");
        }

        if (issue.Labels.All(l => l.Id != labelId))
        {
            issue.Labels.Add(label);
            issue.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        return NoContent();
    }

    [HttpDelete("api/issues/{issueId:guid}/labels/{labelId:guid}")]
    public async Task<IActionResult> Detach(Guid issueId, Guid labelId)
    {
        var issue = await db.Issues.Include(i => i.Labels).SingleOrDefaultAsync(i => i.Id == issueId);
        if (issue is null)
        {
            return NotFound();
        }

        var label = issue.Labels.SingleOrDefault(l => l.Id == labelId);
        if (label is null)
        {
            return NotFound();
        }

        issue.Labels.Remove(label);
        issue.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
