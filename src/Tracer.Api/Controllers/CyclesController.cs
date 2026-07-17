using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Contracts;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

[ApiController]
public class CyclesController(TracerDbContext db) : ControllerBase
{
    /// <summary>
    /// Lists a team's cycles, oldest first. Filtering by <paramref name="status"/>
    /// happens in memory on purpose: status is derived by
    /// <see cref="CycleSchedule.StatusAt"/>, and re-expressing that rule as a SQL
    /// predicate would give the calendar two sources of truth that could drift
    /// apart. A team's cycle count is small enough that this costs nothing.
    /// </summary>
    [HttpGet("api/teams/{teamId:guid}/cycles")]
    public async Task<ActionResult<List<CycleDto>>> ListForTeam(Guid teamId, [FromQuery] CycleStatus? status)
    {
        if (!await db.Teams.AnyAsync(t => t.Id == teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var cycles = await db.Cycles
            .Where(c => c.TeamId == teamId)
            .OrderBy(c => c.Number)
            .ToListAsync();

        var now = DateTimeOffset.UtcNow;
        var dtos = cycles.Select(c => c.ToDto(now));
        if (status is { } wanted)
        {
            dtos = dtos.Where(c => c.Status == wanted);
        }

        return Ok(dtos.ToList());
    }

    [HttpPost("api/teams/{teamId:guid}/cycles")]
    public async Task<ActionResult<CycleDto>> Create(Guid teamId, CreateCycleRequest request)
    {
        if (!await db.Teams.AnyAsync(t => t.Id == teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        // Non-null: [Required] on the nullable properties already rejected omissions.
        var (startsAt, endsAt) = (request.StartsAt!.Value, request.EndsAt!.Value);

        if (!CycleSchedule.IsValidRange(startsAt, endsAt))
        {
            return InvalidRange();
        }

        var siblings = await db.Cycles.Where(c => c.TeamId == teamId).ToListAsync();
        if (FindOverlap(siblings, startsAt, endsAt) is { } clash)
        {
            return OverlapConflict(clash);
        }

        var cycle = new Cycle
        {
            TeamId = teamId,
            Number = siblings.Count == 0 ? 1 : siblings.Max(c => c.Number) + 1,
            Name = request.Name,
            StartsAt = startsAt,
            EndsAt = endsAt,
        };
        db.Cycles.Add(cycle);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = cycle.Id }, cycle.ToDto(DateTimeOffset.UtcNow));
    }

    [HttpGet("api/cycles/{id:guid}")]
    public async Task<ActionResult<CycleDto>> Get(Guid id)
    {
        var cycle = await db.Cycles.FindAsync(id);
        if (cycle is null)
        {
            return this.NotFoundProblem("Cycle", id);
        }

        return Ok(cycle.ToDto(DateTimeOffset.UtcNow));
    }

    [HttpPut("api/cycles/{id:guid}")]
    public async Task<ActionResult<CycleDto>> Update(Guid id, UpdateCycleRequest request)
    {
        var cycle = await db.Cycles.FindAsync(id);
        if (cycle is null)
        {
            return this.NotFoundProblem("Cycle", id);
        }

        var (startsAt, endsAt) = (request.StartsAt!.Value, request.EndsAt!.Value);

        if (!CycleSchedule.IsValidRange(startsAt, endsAt))
        {
            return InvalidRange();
        }

        var siblings = await db.Cycles
            .Where(c => c.TeamId == cycle.TeamId && c.Id != id)
            .ToListAsync();
        if (FindOverlap(siblings, startsAt, endsAt) is { } clash)
        {
            return OverlapConflict(clash);
        }

        cycle.Name = request.Name;
        cycle.StartsAt = startsAt;
        cycle.EndsAt = endsAt;
        await db.SaveChangesAsync();

        return Ok(cycle.ToDto(DateTimeOffset.UtcNow));
    }

    /// <summary>Deleting a cycle unassigns its issues rather than deleting them.</summary>
    [HttpDelete("api/cycles/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var cycle = await db.Cycles.FindAsync(id);
        if (cycle is null)
        {
            return this.NotFoundProblem("Cycle", id);
        }

        db.Cycles.Remove(cycle);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("api/cycles/{id:guid}/summary")]
    public async Task<ActionResult<CycleSummaryDto>> Summary(Guid id)
    {
        var cycle = await db.Cycles.FindAsync(id);
        if (cycle is null)
        {
            return this.NotFoundProblem("Cycle", id);
        }

        var issues = await db.Issues
            .Where(i => i.CycleId == id)
            .Select(i => new { i.Estimate, Type = i.State!.Type })
            .ToListAsync();

        var canceled = issues.Where(i => i.Type == WorkflowStateType.Canceled).ToList();
        var scope = issues.Where(i => i.Type != WorkflowStateType.Canceled).ToList();
        var completed = scope.Where(i => i.Type == WorkflowStateType.Done).ToList();

        var progress = scope.Count == 0
            ? 0
            : Math.Round(completed.Count * 100.0 / scope.Count, 1);

        return Ok(new CycleSummaryDto(
            cycle.Id,
            cycle.TeamId,
            cycle.Number,
            cycle.Name,
            cycle.StartsAt,
            cycle.EndsAt,
            CycleSchedule.StatusAt(cycle.StartsAt, cycle.EndsAt, DateTimeOffset.UtcNow),
            TotalIssues: issues.Count,
            ScopeIssues: scope.Count,
            CompletedIssues: completed.Count,
            InProgressIssues: scope.Count(i => i.Type == WorkflowStateType.InProgress),
            CanceledIssues: canceled.Count,
            ScopeEstimate: scope.Sum(i => i.Estimate ?? 0),
            CompletedEstimate: completed.Sum(i => i.Estimate ?? 0),
            ProgressPercent: progress));
    }

    private static Cycle? FindOverlap(List<Cycle> siblings, DateTimeOffset startsAt, DateTimeOffset endsAt) =>
        siblings.FirstOrDefault(c => CycleSchedule.Overlaps(c.StartsAt, c.EndsAt, startsAt, endsAt));

    private ObjectResult InvalidRange() =>
        this.DomainRuleProblem("Invalid cycle dates.", "A cycle must end after it starts.");

    private ObjectResult OverlapConflict(Cycle clash) =>
        this.ConflictProblem(
            "Overlapping cycle.",
            $"These dates overlap cycle {clash.Number}, which runs from {clash.StartsAt:u} to {clash.EndsAt:u}.");
}
