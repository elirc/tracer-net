using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Contracts;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

[ApiController]
public class IssuesController(TracerDbContext db) : ControllerBase
{
    [HttpGet("api/teams/{teamId:guid}/issues")]
    public async Task<ActionResult<List<IssueDto>>> ListForTeam(Guid teamId)
    {
        var team = await db.Teams.FindAsync(teamId);
        if (team is null)
        {
            return NotFound();
        }

        var issues = await db.Issues
            .Where(i => i.TeamId == teamId)
            .Include(i => i.State)
            .Include(i => i.Labels)
            .OrderBy(i => i.State!.Position)
            .ThenBy(i => i.Position)
            .ToListAsync();

        return Ok(issues.Select(i => i.ToDto(team.Key, i.State!.Name)).ToList());
    }

    [HttpPost("api/teams/{teamId:guid}/issues")]
    public async Task<ActionResult<IssueDto>> Create(Guid teamId, CreateIssueRequest request)
    {
        var team = await db.Teams.FindAsync(teamId);
        if (team is null)
        {
            return NotFound();
        }

        WorkflowState? state;
        if (request.StateId is { } stateId)
        {
            state = await db.WorkflowStates.SingleOrDefaultAsync(s => s.Id == stateId && s.TeamId == teamId);
            if (state is null)
            {
                return ValidationProblem(title: "Unknown workflow state for this team.");
            }
        }
        else
        {
            state = await db.WorkflowStates
                .Where(s => s.TeamId == teamId)
                .OrderBy(s => s.Position)
                .FirstOrDefaultAsync();
            if (state is null)
            {
                return ValidationProblem(title: "Team has no workflow states.");
            }
        }

        if (request.ProjectId is { } projectId
            && !await db.Projects.AnyAsync(p => p.Id == projectId && p.TeamId == teamId))
        {
            return ValidationProblem(title: "Unknown project for this team.");
        }

        var nextNumber = await db.Issues.Where(i => i.TeamId == teamId).MaxAsync(i => (int?)i.Number) ?? 0;
        var nextPosition = await db.Issues
            .Where(i => i.TeamId == teamId && i.StateId == state.Id)
            .MaxAsync(i => (double?)i.Position) ?? 0;

        var issue = new Issue
        {
            TeamId = teamId,
            Number = nextNumber + 1,
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority,
            Estimate = request.Estimate,
            StateId = state.Id,
            ProjectId = request.ProjectId,
            Position = nextPosition + 1,
        };
        db.Issues.Add(issue);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = issue.Id }, issue.ToDto(team.Key, state.Name));
    }

    [HttpGet("api/issues/{id:guid}")]
    public async Task<ActionResult<IssueDto>> Get(Guid id)
    {
        var issue = await db.Issues
            .Include(i => i.Team)
            .Include(i => i.State)
            .Include(i => i.Labels)
            .SingleOrDefaultAsync(i => i.Id == id);
        if (issue is null)
        {
            return NotFound();
        }

        return Ok(issue.ToDto(issue.Team!.Key, issue.State!.Name));
    }

    [HttpPut("api/issues/{id:guid}")]
    public async Task<ActionResult<IssueDto>> Update(Guid id, UpdateIssueRequest request)
    {
        var issue = await db.Issues
            .Include(i => i.Team)
            .Include(i => i.State)
            .Include(i => i.Labels)
            .SingleOrDefaultAsync(i => i.Id == id);
        if (issue is null)
        {
            return NotFound();
        }

        if (request.ProjectId is { } projectId
            && !await db.Projects.AnyAsync(p => p.Id == projectId && p.TeamId == issue.TeamId))
        {
            return ValidationProblem(title: "Unknown project for this team.");
        }

        issue.Title = request.Title;
        issue.Description = request.Description;
        issue.Priority = request.Priority;
        issue.Estimate = request.Estimate;
        issue.ProjectId = request.ProjectId;
        issue.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return Ok(issue.ToDto(issue.Team!.Key, issue.State!.Name));
    }

    /// <summary>
    /// Moves an issue to another workflow state, validating the transition
    /// against <see cref="IssueStateMachine"/>. The issue is appended to the
    /// end of the target state's column.
    /// </summary>
    [HttpPost("api/issues/{id:guid}/transitions")]
    public async Task<ActionResult<IssueDto>> Transition(Guid id, TransitionIssueRequest request)
    {
        var issue = await db.Issues
            .Include(i => i.Team)
            .Include(i => i.State)
            .Include(i => i.Labels)
            .SingleOrDefaultAsync(i => i.Id == id);
        if (issue is null)
        {
            return NotFound();
        }

        var target = await db.WorkflowStates
            .SingleOrDefaultAsync(s => s.Id == request.StateId && s.TeamId == issue.TeamId);
        if (target is null)
        {
            return ValidationProblem(title: "Unknown workflow state for this team.");
        }

        if (!IssueStateMachine.CanTransition(issue.State!.Type, target.Type))
        {
            return UnprocessableEntity(new ProblemDetails
            {
                Title = "Invalid state transition.",
                Detail = $"Cannot move an issue from '{issue.State.Name}' ({issue.State.Type}) to '{target.Name}' ({target.Type}).",
                Status = StatusCodes.Status422UnprocessableEntity,
            });
        }

        if (issue.StateId != target.Id)
        {
            var nextPosition = await db.Issues
                .Where(i => i.TeamId == issue.TeamId && i.StateId == target.Id)
                .MaxAsync(i => (double?)i.Position) ?? 0;
            issue.StateId = target.Id;
            issue.State = target;
            issue.Position = nextPosition + 1;
            issue.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        return Ok(issue.ToDto(issue.Team!.Key, target.Name));
    }

    [HttpDelete("api/issues/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var issue = await db.Issues.FindAsync(id);
        if (issue is null)
        {
            return NotFound();
        }

        db.Issues.Remove(issue);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
