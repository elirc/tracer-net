using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// Roadmap milestones: dated targets a project's issues are gathered under.
///
/// <para>
/// Progress is never stored. Every read recomputes it from the issues currently
/// pointing at the milestone, so a milestone cannot claim to be 80% done while its
/// board says otherwise — there is no second copy of the truth to fall behind.
/// </para>
/// </summary>
[ApiController]
public class MilestonesController(TracerDbContext db, TeamAccess access) : ControllerBase
{
    /// <summary>
    /// A team's roadmap: its milestones in target-date order, each with progress
    /// rolled up. Optionally narrowed to one project or one derived status.
    /// </summary>
    [HttpGet("api/teams/{teamId:guid}/milestones")]
    public async Task<ActionResult<List<MilestoneDto>>> ListForTeam(
        Guid teamId,
        [FromQuery] Guid? projectId,
        [FromQuery] MilestoneStatus? status)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var milestones = await db.Milestones
            .Where(m => m.TeamId == teamId && (projectId == null || m.ProjectId == projectId))
            .OrderBy(m => m.TargetDate)
            .ThenBy(m => m.CreatedAt)
            .ToListAsync();

        var typesByMilestone = await IssueStateTypesByMilestoneAsync(teamId);

        var now = DateTimeOffset.UtcNow;
        var dtos = milestones.Select(m => ToDto(m, typesByMilestone.GetValueOrDefault(m.Id, []), now));

        // Status is derived, so it is filtered here rather than in SQL — the same
        // reason cycle status is. A team's milestone count is small.
        if (status is { } wanted)
        {
            dtos = dtos.Where(d => d.Status == wanted);
        }

        return Ok(dtos.ToList());
    }

    [HttpPost("api/projects/{projectId:guid}/milestones")]
    public async Task<ActionResult<MilestoneDto>> Create(Guid projectId, CreateMilestoneRequest request)
    {
        var project = await db.Projects.FindAsync(projectId);
        if (project is null || !await access.CanAccessTeamAsync(User, project.TeamId))
        {
            return this.NotFoundProblem("Project", projectId);
        }

        var milestone = new Milestone
        {
            TeamId = project.TeamId,
            ProjectId = project.Id,
            Name = request.Name,
            Description = request.Description,
            // Non-null: [Required] on the nullable property already rejected an omission.
            TargetDate = request.TargetDate!.Value,
        };
        db.Milestones.Add(milestone);
        await db.SaveChangesAsync();

        // Freshly created: no issues point at it yet, so progress rolls up from none.
        return CreatedAtAction(nameof(Get), new { id = milestone.Id }, ToDto(milestone, [], DateTimeOffset.UtcNow));
    }

    [HttpGet("api/milestones/{id:guid}")]
    public async Task<ActionResult<MilestoneDto>> Get(Guid id)
    {
        var milestone = await db.Milestones.FindAsync(id);
        if (milestone is null || !await access.CanAccessTeamAsync(User, milestone.TeamId))
        {
            return this.NotFoundProblem("Milestone", id);
        }

        var types = await IssueStateTypesAsync(id);
        return Ok(ToDto(milestone, types, DateTimeOffset.UtcNow));
    }

    [HttpPut("api/milestones/{id:guid}")]
    public async Task<ActionResult<MilestoneDto>> Update(Guid id, UpdateMilestoneRequest request)
    {
        var milestone = await db.Milestones.FindAsync(id);
        if (milestone is null || !await access.CanAccessTeamAsync(User, milestone.TeamId))
        {
            return this.NotFoundProblem("Milestone", id);
        }

        milestone.Name = request.Name;
        milestone.Description = request.Description;
        milestone.TargetDate = request.TargetDate!.Value;
        await db.SaveChangesAsync();

        var types = await IssueStateTypesAsync(id);
        return Ok(ToDto(milestone, types, DateTimeOffset.UtcNow));
    }

    /// <summary>Deleting a milestone releases its issues rather than deleting them.</summary>
    [HttpDelete("api/milestones/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var milestone = await db.Milestones.FindAsync(id);
        if (milestone is null || !await access.CanAccessTeamAsync(User, milestone.TeamId))
        {
            return this.NotFoundProblem("Milestone", id);
        }

        db.Milestones.Remove(milestone);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<List<WorkflowStateType>> IssueStateTypesAsync(Guid milestoneId) =>
        await db.Issues
            .Where(i => i.MilestoneId == milestoneId)
            .Select(i => i.State!.Type)
            .ToListAsync();

    private async Task<Dictionary<Guid, List<WorkflowStateType>>> IssueStateTypesByMilestoneAsync(Guid teamId)
    {
        var rows = await db.Issues
            .Where(i => i.TeamId == teamId && i.MilestoneId != null)
            .Select(i => new { MilestoneId = i.MilestoneId!.Value, Type = i.State!.Type })
            .ToListAsync();

        return rows
            .GroupBy(r => r.MilestoneId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Type).ToList());
    }

    private static MilestoneDto ToDto(Milestone milestone, IReadOnlyList<WorkflowStateType> issueStateTypes, DateTimeOffset now)
    {
        var progress = MilestoneRoadmap.Evaluate(issueStateTypes, milestone.TargetDate, now);
        return new MilestoneDto(
            milestone.Id,
            milestone.TeamId,
            milestone.ProjectId,
            milestone.Name,
            milestone.Description,
            milestone.TargetDate,
            milestone.CreatedAt,
            progress.TotalIssues,
            progress.ScopeIssues,
            progress.CompletedIssues,
            progress.ProgressPercent,
            progress.Status);
    }
}
