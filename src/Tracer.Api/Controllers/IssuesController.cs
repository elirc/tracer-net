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
    /// <summary>Escape character for LIKE patterns; free-text input may contain % or _.</summary>
    private const string LikeEscape = "\\";

    [HttpGet("api/teams/{teamId:guid}/issues")]
    public async Task<ActionResult<List<IssueDto>>> ListForTeam(Guid teamId)
    {
        var team = await db.Teams.FindAsync(teamId);
        if (team is null)
        {
            return this.NotFoundProblem("Team", teamId);
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

    /// <summary>
    /// Searches issues across teams. Every filter is optional and combines with
    /// AND; results are paged and always ordered deterministically.
    /// </summary>
    [HttpGet("api/issues")]
    public async Task<ActionResult<PagedResult<IssueDto>>> Search([FromQuery] IssueSearchQuery query)
    {
        var issues = db.Issues.AsQueryable();

        if (query.TeamId is { } teamId)
        {
            issues = issues.Where(i => i.TeamId == teamId);
        }

        if (query.ProjectId is { } projectId)
        {
            issues = issues.Where(i => i.ProjectId == projectId);
        }

        if (query.StateId is { } stateId)
        {
            issues = issues.Where(i => i.StateId == stateId);
        }

        if (query.CycleId is { } cycleId)
        {
            issues = issues.Where(i => i.CycleId == cycleId);
        }

        if (query.LabelId is { } labelId)
        {
            issues = issues.Where(i => i.Labels.Any(l => l.Id == labelId));
        }

        if (query.Priority is { } priority)
        {
            issues = issues.Where(i => i.Priority == priority);
        }

        if (!string.IsNullOrWhiteSpace(query.Assignee))
        {
            var assignee = query.Assignee.Trim().ToLower();
            issues = issues.Where(i => i.Assignee != null && i.Assignee.ToLower() == assignee);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = $"%{EscapeLike(query.Q.Trim())}%";
            issues = issues.Where(i =>
                EF.Functions.Like(i.Title, pattern, LikeEscape) ||
                (i.Description != null && EF.Functions.Like(i.Description, pattern, LikeEscape)));
        }

        var total = await issues.CountAsync();

        var results = await ApplySort(issues, query.Sort, query.Order)
            .Include(i => i.Team)
            .Include(i => i.State)
            .Include(i => i.Labels)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return Ok(new PagedResult<IssueDto>(
            results.Select(i => i.ToDto(i.Team!.Key, i.State!.Name)).ToList(),
            query.Page,
            query.PageSize,
            total,
            (int)Math.Ceiling(total / (double)query.PageSize)));
    }

    [HttpPost("api/teams/{teamId:guid}/issues")]
    public async Task<ActionResult<IssueDto>> Create(Guid teamId, CreateIssueRequest request)
    {
        var team = await db.Teams.FindAsync(teamId);
        if (team is null)
        {
            return this.NotFoundProblem("Team", teamId);
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

        if (request.CycleId is { } cycleId
            && !await db.Cycles.AnyAsync(c => c.Id == cycleId && c.TeamId == teamId))
        {
            return ValidationProblem(title: "Unknown cycle for this team.");
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
            Assignee = request.Assignee,
            StateId = state.Id,
            ProjectId = request.ProjectId,
            CycleId = request.CycleId,
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
            return this.NotFoundProblem("Issue", id);
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
            return this.NotFoundProblem("Issue", id);
        }

        if (request.ProjectId is { } projectId
            && !await db.Projects.AnyAsync(p => p.Id == projectId && p.TeamId == issue.TeamId))
        {
            return ValidationProblem(title: "Unknown project for this team.");
        }

        if (request.CycleId is { } cycleId
            && !await db.Cycles.AnyAsync(c => c.Id == cycleId && c.TeamId == issue.TeamId))
        {
            return ValidationProblem(title: "Unknown cycle for this team.");
        }

        issue.Title = request.Title;
        issue.Description = request.Description;
        issue.Priority = request.Priority;
        issue.Estimate = request.Estimate;
        issue.Assignee = request.Assignee;
        issue.ProjectId = request.ProjectId;
        issue.CycleId = request.CycleId;
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
            return this.NotFoundProblem("Issue", id);
        }

        var target = await db.WorkflowStates
            .SingleOrDefaultAsync(s => s.Id == request.StateId!.Value && s.TeamId == issue.TeamId);
        if (target is null)
        {
            return ValidationProblem(title: "Unknown workflow state for this team.");
        }

        if (!IssueStateMachine.CanTransition(issue.State!.Type, target.Type))
        {
            return InvalidTransition(issue.State, target);
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

    /// <summary>
    /// Places an issue between two neighbours, optionally in a different column.
    /// Omitting both neighbours appends to the end of the target column.
    ///
    /// Dragging a card into another column is still a state change, so it is
    /// validated by <see cref="IssueStateMachine"/> exactly like an explicit
    /// transition: reordering must not become a back door around the workflow.
    /// </summary>
    [HttpPost("api/issues/{id:guid}/reorder")]
    public async Task<ActionResult<IssueDto>> Reorder(Guid id, ReorderIssueRequest request)
    {
        var issue = await db.Issues
            .Include(i => i.Team)
            .Include(i => i.State)
            .Include(i => i.Labels)
            .SingleOrDefaultAsync(i => i.Id == id);
        if (issue is null)
        {
            return this.NotFoundProblem("Issue", id);
        }

        var target = issue.State!;
        if (request.StateId is { } stateId && stateId != issue.StateId)
        {
            var requested = await db.WorkflowStates
                .SingleOrDefaultAsync(s => s.Id == stateId && s.TeamId == issue.TeamId);
            if (requested is null)
            {
                return ValidationProblem(title: "Unknown workflow state for this team.");
            }

            if (!IssueStateMachine.CanTransition(issue.State.Type, requested.Type))
            {
                return InvalidTransition(issue.State, requested);
            }

            target = requested;
        }

        var column = await db.Issues
            .Where(i => i.TeamId == issue.TeamId && i.StateId == target.Id && i.Id != issue.Id)
            .OrderBy(i => i.Position)
            .ToListAsync();

        Issue? after = null;
        if (request.AfterIssueId is { } afterId)
        {
            after = column.SingleOrDefault(i => i.Id == afterId);
            if (after is null)
            {
                return ValidationProblem(title: "AfterIssueId is not another issue in the target column.");
            }
        }

        Issue? before = null;
        if (request.BeforeIssueId is { } beforeId)
        {
            before = column.SingleOrDefault(i => i.Id == beforeId);
            if (before is null)
            {
                return ValidationProblem(title: "BeforeIssueId is not another issue in the target column.");
            }
        }

        if (after is not null && before is not null && after.Position >= before.Position)
        {
            return ValidationProblem(title: "AfterIssueId must come before BeforeIssueId in the target column.");
        }

        // Recomputed after a rebalance, so the bounds reflect the new ranks.
        (double? Lower, double? Upper) Bounds()
        {
            if (after is not null && before is not null)
            {
                return (after.Position, before.Position);
            }

            if (after is not null)
            {
                return (after.Position, column.FirstOrDefault(i => i.Position > after.Position)?.Position);
            }

            if (before is not null)
            {
                return (column.LastOrDefault(i => i.Position < before.Position)?.Position, before.Position);
            }

            return (column.LastOrDefault()?.Position, null);
        }

        var (lower, upper) = Bounds();
        if (IssueRanker.NeedsRebalance(lower, upper))
        {
            // The neighbours have been split so many times that no rank fits
            // between them. Spread the column back out, then try again; the
            // rebalance rides along in the same transaction as the move.
            for (var i = 0; i < column.Count; i++)
            {
                column[i].Position = IssueRanker.RankAt(i);
            }

            (lower, upper) = Bounds();
        }

        issue.StateId = target.Id;
        issue.State = target;
        issue.Position = IssueRanker.Between(lower, upper);
        issue.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return Ok(issue.ToDto(issue.Team!.Key, target.Name));
    }

    [HttpDelete("api/issues/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var issue = await db.Issues.FindAsync(id);
        if (issue is null)
        {
            return this.NotFoundProblem("Issue", id);
        }

        db.Issues.Remove(issue);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private ObjectResult InvalidTransition(WorkflowState from, WorkflowState to) =>
        this.DomainRuleProblem(
            "Invalid state transition.",
            $"Cannot move an issue from '{from.Name}' ({from.Type}) to '{to.Name}' ({to.Type}).");

    /// <summary>
    /// Ties are broken by id so that paging through a sorted result cannot
    /// repeat or skip rows when several issues share a sort key.
    /// </summary>
    private static IQueryable<Issue> ApplySort(IQueryable<Issue> issues, IssueSortField sort, SortDirection order)
    {
        var desc = order == SortDirection.Desc;

        IOrderedQueryable<Issue> sorted = sort switch
        {
            IssueSortField.Created => desc
                ? issues.OrderByDescending(i => i.CreatedAt)
                : issues.OrderBy(i => i.CreatedAt),
            IssueSortField.Number => desc
                ? issues.OrderByDescending(i => i.Number)
                : issues.OrderBy(i => i.Number),
            IssueSortField.Title => desc
                ? issues.OrderByDescending(i => i.Title)
                : issues.OrderBy(i => i.Title),
            // "No priority" is an absence, not the lowest urgency, so it sorts
            // last ascending rather than jumping ahead of Urgent.
            IssueSortField.Priority => desc
                ? issues.OrderByDescending(i => i.Priority == IssuePriority.None ? 5 : (int)i.Priority)
                : issues.OrderBy(i => i.Priority == IssuePriority.None ? 5 : (int)i.Priority),
            // Board order: state columns left to right, then rank within a column.
            IssueSortField.Position => desc
                ? issues.OrderByDescending(i => i.State!.Position).ThenByDescending(i => i.Position)
                : issues.OrderBy(i => i.State!.Position).ThenBy(i => i.Position),
            _ => desc
                ? issues.OrderByDescending(i => i.UpdatedAt)
                : issues.OrderBy(i => i.UpdatedAt),
        };

        return sorted.ThenBy(i => i.Id);
    }

    private static string EscapeLike(string value) => value
        .Replace(LikeEscape, LikeEscape + LikeEscape)
        .Replace("%", LikeEscape + "%")
        .Replace("_", LikeEscape + "_");
}
