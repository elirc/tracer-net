using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Api.Search;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

[ApiController]
public class IssuesController(TracerDbContext db, TeamAccess access, ActivityRecorder activity) : ControllerBase
{
    [HttpGet("api/teams/{teamId:guid}/issues")]
    public async Task<ActionResult<PagedResult<IssueDto>>> ListForTeam(Guid teamId, [FromQuery] PageQuery paging)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var team = await db.Teams.FindAsync(teamId);

        // Include before paging: on a collection include EF applies Skip/Take to
        // the joined rows rather than the issues, so an issue with three labels
        // would eat three slots of a page. The id tiebreak keeps a board that
        // shares state/position ranks from repeating or skipping rows across pages.
        var issues = await db.Issues
            .Where(i => i.TeamId == teamId)
            .Include(i => i.State)
            .Include(i => i.Labels)
            .OrderBy(i => i.State!.Position)
            .ThenBy(i => i.Position)
            .ThenBy(i => i.Id)
            .ToPagedResultAsync(paging, i => i.ToDto(team!.Key, i.State!.Name));

        return Ok(issues);
    }

    /// <summary>
    /// Searches issues across teams. Every filter is optional and combines with
    /// AND; results are paged and always ordered deterministically.
    /// </summary>
    [HttpGet("api/issues")]
    public async Task<ActionResult<PagedResult<IssueDto>>> Search([FromQuery] IssueSearchQuery query)
    {
        var issues = db.Issues.AsQueryable();

        // Scope before filtering, not after paging. Search is the one endpoint
        // that reads across teams without the caller naming an id, so this
        // filter — not a check on the way out — is what keeps another team's
        // issues out of the results, and out of `total` along with them.
        if (!User.IsAdmin())
        {
            var mine = await access.MemberTeamIdsAsync(User);
            issues = issues.Where(i => mine.Contains(i.TeamId));
        }

        if (query.TeamId is { } teamId)
        {
            issues = issues.Where(i => i.TeamId == teamId);
        }

        return Ok(await IssueSearch.PageAsync(issues, query, query.Page, query.PageSize));
    }

    [HttpPost("api/teams/{teamId:guid}/issues")]
    public async Task<ActionResult<IssueDto>> Create(Guid teamId, CreateIssueRequest request)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var team = await db.Teams.FindAsync(teamId);

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

        if (request.MilestoneId is { } newMilestoneId
            && !await db.Milestones.AnyAsync(m => m.Id == newMilestoneId && m.TeamId == teamId))
        {
            return ValidationProblem(title: "Unknown milestone for this team.");
        }

        if (request.ParentId is { } newParentId
            && !await db.Issues.AnyAsync(i => i.Id == newParentId && i.TeamId == teamId))
        {
            return UnknownParent();
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
            MilestoneId = request.MilestoneId,
            ParentId = request.ParentId,
            Position = nextPosition + 1,
        };
        db.Issues.Add(issue);
        await activity.RecordAsync(User, issue, ActivityType.IssueCreated, newValue: issue.Title);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = issue.Id }, issue.ToDto(team!.Key, state.Name));
    }

    [HttpGet("api/issues/{id:guid}")]
    public async Task<ActionResult<IssueDto>> Get(Guid id)
    {
        var issue = await db.Issues
            .Include(i => i.Team)
            .Include(i => i.State)
            .Include(i => i.Labels)
            .SingleOrDefaultAsync(i => i.Id == id);
        if (issue is null || !await access.CanAccessTeamAsync(User, issue.TeamId))
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
        if (issue is null || !await access.CanAccessTeamAsync(User, issue.TeamId))
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

        if (request.MilestoneId is { } milestoneId
            && !await db.Milestones.AnyAsync(m => m.Id == milestoneId && m.TeamId == issue.TeamId))
        {
            return ValidationProblem(title: "Unknown milestone for this team.");
        }

        if (request.ParentId is { } parentId && parentId != issue.ParentId)
        {
            if (!await db.Issues.AnyAsync(i => i.Id == parentId && i.TeamId == issue.TeamId))
            {
                return UnknownParent();
            }

            if (await WouldCycleAsync(issue, parentId))
            {
                return this.DomainRuleProblem(
                    "Circular sub-issue.",
                    parentId == issue.Id
                        ? "An issue cannot be its own parent."
                        : "That issue is already somewhere below this one, so this would close a loop.");
            }
        }

        // Captured before anything is written: once the entity is mutated the
        // previous values are gone, and "changed from X to Y" is the entire
        // reason anyone reads an audit log.
        var before = new
        {
            issue.Title,
            issue.Description,
            issue.Priority,
            issue.Estimate,
            issue.Assignee,
            issue.ParentId,
        };

        // If the caller told us which version they edited, hold the update to it:
        // EF checks the token it read, so pointing that at the caller's expected
        // value turns "someone changed this since you loaded it" into a 409 rather
        // than a silent overwrite of their change.
        if (request.Version is { } expected)
        {
            db.Entry(issue).Property(i => i.Version).OriginalValue = expected;
        }

        issue.Title = request.Title;
        issue.Description = request.Description;
        issue.Priority = request.Priority;
        issue.Estimate = request.Estimate;
        issue.Assignee = request.Assignee;
        issue.ProjectId = request.ProjectId;
        issue.CycleId = request.CycleId;
        issue.MilestoneId = request.MilestoneId;
        issue.ParentId = request.ParentId;
        issue.UpdatedAt = DateTimeOffset.UtcNow;
        issue.Version = Guid.NewGuid();

        await RecordEditsAsync(issue, before.Title, before.Description, before.Priority, before.Estimate);

        // Assignment and re-parenting are not field edits, they are things that
        // happen to a person or a plan, so they get their own event types rather
        // than being flattened into "someone changed a column".
        if (!string.Equals(before.Assignee, issue.Assignee, StringComparison.Ordinal))
        {
            await activity.RecordAsync(User, issue, ActivityType.IssueAssigned,
                oldValue: before.Assignee, newValue: issue.Assignee);
        }

        if (before.ParentId != issue.ParentId)
        {
            await activity.RecordAsync(User, issue, ActivityType.IssueParentChanged,
                oldValue: await IdentifierOfAsync(before.ParentId),
                newValue: await IdentifierOfAsync(issue.ParentId));
        }

        if (!await TrySaveAsync())
        {
            return this.ConcurrencyConflictProblem("Issue");
        }

        return Ok(issue.ToDto(issue.Team!.Key, issue.State!.Name));
    }

    /// <summary>
    /// Saves, translating the optimistic-concurrency failure into a caller-visible
    /// signal rather than letting it bubble up as a 500. EF raises this when a
    /// write's <c>WHERE Version = …</c> matches no row — i.e. the issue was edited
    /// between the caller reading it and saving. Returns false so the caller can
    /// answer 409; any other failure still throws and becomes a 500, because it is
    /// one.
    /// </summary>
    private async Task<bool> TrySaveAsync()
    {
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>
    /// One entry per field that actually moved. A PUT that resends an issue
    /// unchanged — which is what a form does every time someone hits save —
    /// records nothing, because nothing happened; a feed that says "ana updated
    /// this" fourteen times for one edit is a feed people stop reading.
    /// </summary>
    private Task RecordEditsAsync(
        Issue issue,
        string? oldTitle,
        string? oldDescription,
        IssuePriority oldPriority,
        int? oldEstimate) =>
        activity.RecordFieldEditsAsync(User, issue, oldTitle, oldDescription, oldPriority, oldEstimate);

    /// <summary>Renders an issue id as "ENG-42" for the log, or null when there was no issue.</summary>
    private async Task<string?> IdentifierOfAsync(Guid? issueId)
    {
        if (issueId is not { } id)
        {
            return null;
        }

        var found = await db.Issues
            .Where(i => i.Id == id)
            .Select(i => new { i.Number, TeamKey = i.Team!.Key })
            .SingleOrDefaultAsync();

        return found is null ? null : $"{found.TeamKey}-{found.Number}";
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
        if (issue is null || !await access.CanAccessTeamAsync(User, issue.TeamId))
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
            var previous = issue.State.Name;
            issue.StateId = target.Id;
            issue.State = target;
            issue.Position = nextPosition + 1;
            issue.UpdatedAt = DateTimeOffset.UtcNow;
            issue.Version = Guid.NewGuid();
            await activity.RecordAsync(User, issue, ActivityType.IssueStateChanged, oldValue: previous, newValue: target.Name);
            if (!await TrySaveAsync())
            {
                return this.ConcurrencyConflictProblem("Issue");
            }
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
        if (issue is null || !await access.CanAccessTeamAsync(User, issue.TeamId))
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

        var leftColumn = issue.State!.Name;
        var changedColumn = issue.StateId != target.Id;

        issue.StateId = target.Id;
        issue.State = target;
        issue.Position = IssueRanker.Between(lower, upper);
        issue.UpdatedAt = DateTimeOffset.UtcNow;
        issue.Version = Guid.NewGuid();

        // Dragging a card to another column is a state change and is logged as
        // one — the same event an explicit transition produces, because to
        // everyone reading the feed it is the same thing. Moving a card *within*
        // a column is not: rank is a view preference, and logging it would bury
        // the feed under the noise of one person tidying their board.
        if (changedColumn)
        {
            await activity.RecordAsync(User, issue, ActivityType.IssueStateChanged,
                oldValue: leftColumn, newValue: target.Name);
        }

        if (!await TrySaveAsync())
        {
            return this.ConcurrencyConflictProblem("Issue");
        }

        return Ok(issue.ToDto(issue.Team!.Key, target.Name));
    }

    /// <summary>
    /// An issue's sub-issues, with the roll-up a parent needs to show progress.
    ///
    /// The roll-up lives here rather than on <see cref="IssueDto"/> so that
    /// listing a board stays one query. Hanging it off every issue in a list
    /// would mean counting children per row — the N+1 this product has an open
    /// ticket about — to answer a question only an issue's own page asks.
    ///
    /// Not paged: the roll-up is over the whole set of direct children, so the
    /// read is the roll-up's own input rather than an open-ended list, and a
    /// paged subset would make the percentages describe a page instead of the
    /// issue. Direct children of one issue are bounded by that issue's own fan-out.
    /// </summary>
    [HttpGet("api/issues/{id:guid}/children")]
    public async Task<ActionResult<SubIssuesDto>> Children(Guid id)
    {
        var parent = await db.Issues.Include(i => i.Team).SingleOrDefaultAsync(i => i.Id == id);
        if (parent is null || !await access.CanAccessTeamAsync(User, parent.TeamId))
        {
            return this.NotFoundProblem("Issue", id);
        }

        var children = await db.Issues
            .Where(i => i.ParentId == id)
            .Include(i => i.State)
            .Include(i => i.Labels)
            .OrderBy(i => i.State!.Position)
            .ThenBy(i => i.Position)
            .ToListAsync();

        var canceled = children.Where(i => i.State!.Type == WorkflowStateType.Canceled).ToList();
        var scope = children.Where(i => i.State!.Type != WorkflowStateType.Canceled).ToList();
        var completed = scope.Where(i => i.State!.Type == WorkflowStateType.Done).ToList();

        var rollup = new SubIssueRollupDto(
            TotalIssues: children.Count,
            ScopeIssues: scope.Count,
            CompletedIssues: completed.Count,
            InProgressIssues: scope.Count(i => i.State!.Type == WorkflowStateType.InProgress),
            CanceledIssues: canceled.Count,
            ScopeEstimate: scope.Sum(i => i.Estimate ?? 0),
            CompletedEstimate: completed.Sum(i => i.Estimate ?? 0),
            ProgressPercent: scope.Count == 0 ? 0 : Math.Round(completed.Count * 100.0 / scope.Count, 1));

        return Ok(new SubIssuesDto(
            rollup,
            children.Select(i => i.ToDto(parent.Team!.Key, i.State!.Name)).ToList()));
    }

    [HttpDelete("api/issues/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var issue = await db.Issues.FindAsync(id);
        if (issue is null || !await access.CanAccessTeamAsync(User, issue.TeamId))
        {
            return this.NotFoundProblem("Issue", id);
        }

        // Recorded before the row goes, and committed in the same transaction as
        // the removal. The log carries no foreign key to the issue precisely so
        // that this entry outlives it — "who deleted this?" is the question an
        // audit log exists for.
        await activity.RecordAsync(User, issue, ActivityType.IssueDeleted, oldValue: issue.Title);
        db.Issues.Remove(issue);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private ObjectResult InvalidTransition(WorkflowState from, WorkflowState to) =>
        this.DomainRuleProblem(
            "Invalid state transition.",
            $"Cannot move an issue from '{from.Name}' ({from.Type}) to '{to.Name}' ({to.Type}).");

    /// <summary>
    /// A parent from another team is a 400, not a 404: the caller may well be
    /// able to see it, it is simply not theirs to point at from here. That is
    /// the same answer this controller already gives for another team's state,
    /// project, or cycle.
    /// </summary>
    private ActionResult UnknownParent() =>
        ValidationProblem(title: "Unknown parent issue for this team.");

    /// <summary>
    /// True when re-parenting <paramref name="issue"/> under
    /// <paramref name="parentId"/> would close a loop.
    ///
    /// The team's parent edges are loaded in one query and walked in memory
    /// rather than climbing the chain with a query per level: the chain is short,
    /// but a query per level makes the cost of a legal move depend on how deeply
    /// somebody else happened to nest things.
    /// </summary>
    private async Task<bool> WouldCycleAsync(Issue issue, Guid parentId)
    {
        var edges = await db.Issues
            .Where(i => i.TeamId == issue.TeamId && i.ParentId != null)
            .Select(i => new { i.Id, ParentId = i.ParentId!.Value })
            .ToDictionaryAsync(i => i.Id, i => i.ParentId);

        return IssueGraph.WouldCycle(edges, issue.Id, parentId);
    }
}
