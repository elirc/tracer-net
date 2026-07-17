using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Api.Search;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// Saved views: a named filter set a team can share, or a person can keep.
///
/// <para>
/// A view stores rules, not results. Executing one runs the rules through the
/// same <see cref="IssueSearch"/> that <c>GET /api/issues</c> uses, so a view
/// can never drift from search — it cannot escape wildcards differently, order
/// ties differently, or page differently, because it is not a second
/// implementation of any of it.
/// </para>
/// </summary>
[ApiController]
public class SavedViewsController(TracerDbContext db, TeamAccess access) : ControllerBase
{
    /// <summary>
    /// The team's shared views plus the caller's own. Another member's personal
    /// views are not listed, for the same reason they cannot be fetched.
    /// </summary>
    [HttpGet("api/teams/{teamId:guid}/views")]
    public async Task<ActionResult<List<SavedViewDto>>> ListForTeam(Guid teamId)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var callerId = User.UserId();
        var views = await db.SavedViews
            .Where(v => v.TeamId == teamId
                && (v.Scope == SavedViewScope.Team || v.OwnerUserId == callerId))
            .Include(v => v.Owner)
            .OrderByDescending(v => v.IsDefault)
            .ThenBy(v => v.Name)
            .ThenBy(v => v.Id)
            .ToListAsync();

        return Ok(views.Select(v => v.ToDto()).ToList());
    }

    /// <summary>The team's default view, or 404 when the team has not set one.</summary>
    [HttpGet("api/teams/{teamId:guid}/views/default")]
    public async Task<ActionResult<SavedViewDto>> GetDefault(Guid teamId)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var view = await db.SavedViews
            .Include(v => v.Owner)
            .SingleOrDefaultAsync(v => v.TeamId == teamId && v.IsDefault);
        if (view is null)
        {
            return this.Problem(
                title: "No default view.",
                detail: $"Team {teamId} has not set a default view.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(view.ToDto());
    }

    [HttpPost("api/teams/{teamId:guid}/views")]
    public async Task<ActionResult<SavedViewDto>> Create(Guid teamId, CreateSavedViewRequest request)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var rules = request.Rules ?? new IssueFilter();
        if (await ValidateRulesAsync(teamId, rules) is { } invalid)
        {
            return invalid;
        }

        if (request.IsDefault && !SavedViewRules.CanBeDefault(request.Scope))
        {
            return PersonalDefaultProblem();
        }

        var view = new SavedView
        {
            TeamId = teamId,
            Name = request.Name,
            Scope = request.Scope,
            OwnerUserId = SavedViewRules.OwnerFor(request.Scope, User.UserId()),
            FilterJson = SavedViewMappings.Serialize(rules),
        };
        db.SavedViews.Add(view);

        await SaveWithDefaultAsync(view, request.IsDefault);
        await db.Entry(view).Reference(v => v.Owner).LoadAsync();

        return CreatedAtAction(nameof(Get), new { id = view.Id }, view.ToDto());
    }

    [HttpGet("api/views/{id:guid}")]
    public async Task<ActionResult<SavedViewDto>> Get(Guid id)
    {
        var view = await FindVisibleAsync(id);
        if (view is null)
        {
            return this.NotFoundProblem("View", id);
        }

        return Ok(view.ToDto());
    }

    [HttpPut("api/views/{id:guid}")]
    public async Task<ActionResult<SavedViewDto>> Update(Guid id, UpdateSavedViewRequest request)
    {
        var view = await FindVisibleAsync(id);
        if (view is null)
        {
            return this.NotFoundProblem("View", id);
        }

        var rules = request.Rules ?? new IssueFilter();
        if (await ValidateRulesAsync(view.TeamId, rules) is { } invalid)
        {
            return invalid;
        }

        if (request.IsDefault && !SavedViewRules.CanBeDefault(request.Scope))
        {
            return PersonalDefaultProblem();
        }

        view.Name = request.Name;
        view.Scope = request.Scope;
        // Re-derived rather than preserved: a view made personal becomes the
        // caller's, and one shared with the team stops being anyone's.
        view.OwnerUserId = SavedViewRules.OwnerFor(request.Scope, User.UserId());
        view.FilterJson = SavedViewMappings.Serialize(rules);
        view.UpdatedAt = DateTimeOffset.UtcNow;

        await SaveWithDefaultAsync(view, request.IsDefault);
        await db.Entry(view).Reference(v => v.Owner).LoadAsync();

        return Ok(view.ToDto());
    }

    [HttpDelete("api/views/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var view = await FindVisibleAsync(id);
        if (view is null)
        {
            return this.NotFoundProblem("View", id);
        }

        db.SavedViews.Remove(view);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Runs the view: its rules, the caller's page. The result is the same shape
    /// <c>GET /api/issues</c> returns, because it is the same read.
    /// </summary>
    [HttpGet("api/views/{id:guid}/issues")]
    public async Task<ActionResult<PagedResult<IssueDto>>> Execute(
        Guid id,
        [FromQuery] ExecuteSavedViewQuery query)
    {
        var view = await FindVisibleAsync(id);
        if (view is null)
        {
            return this.NotFoundProblem("View", id);
        }

        // The view's own team is the scope, and the rules cannot widen it:
        // IssueFilter has no team field to widen it with.
        var issues = db.Issues.Where(i => i.TeamId == view.TeamId);

        return Ok(await IssueSearch.PageAsync(
            issues,
            SavedViewMappings.Deserialize(view.FilterJson),
            query.Page,
            query.PageSize));
    }

    /// <summary>
    /// A view the caller may see: on a team they can reach, and either shared
    /// with that team or their own. Everything else is 404 — including another
    /// member's personal view, whose existence is not the caller's business.
    /// </summary>
    private async Task<SavedView?> FindVisibleAsync(Guid id)
    {
        var view = await db.SavedViews
            .Include(v => v.Owner)
            .SingleOrDefaultAsync(v => v.Id == id);

        if (view is null || !await access.CanAccessTeamAsync(User, view.TeamId))
        {
            return null;
        }

        return SavedViewRules.CanSee(view.Scope, view.OwnerUserId, User.UserId()) ? view : null;
    }

    /// <summary>
    /// Saves the view, promoting it to the team default if asked.
    ///
    /// The demotion of the outgoing default is written before the promotion, in
    /// its own round trip, because the filtered unique index means two defaults
    /// cannot coexist even momentarily. Both statements share one transaction, so
    /// a failure between them cannot leave the team with no default at all.
    /// </summary>
    private async Task SaveWithDefaultAsync(SavedView view, bool isDefault)
    {
        if (!isDefault)
        {
            view.IsDefault = false;
            await db.SaveChangesAsync();
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync();

        var outgoing = await db.SavedViews
            .Where(v => v.TeamId == view.TeamId && v.IsDefault && v.Id != view.Id)
            .ToListAsync();
        foreach (var previous in outgoing)
        {
            previous.IsDefault = false;
        }

        view.IsDefault = false;
        await db.SaveChangesAsync();

        view.IsDefault = true;
        await db.SaveChangesAsync();

        await tx.CommitAsync();
    }

    /// <summary>
    /// Rejects rules that point at another team's data. Without this a view can
    /// be saved that is guaranteed to match nothing forever, and the caller is
    /// told it worked — the failure surfaces later as an empty list with no
    /// explanation. 400 rather than 404: the id names something real, just not
    /// something this view may point at.
    /// </summary>
    private async Task<ActionResult?> ValidateRulesAsync(Guid teamId, IssueFilter rules)
    {
        if (rules.ProjectId is { } projectId
            && !await db.Projects.AnyAsync(p => p.Id == projectId && p.TeamId == teamId))
        {
            return ValidationProblem(title: "Unknown project for this team.");
        }

        if (rules.StateId is { } stateId
            && !await db.WorkflowStates.AnyAsync(s => s.Id == stateId && s.TeamId == teamId))
        {
            return ValidationProblem(title: "Unknown workflow state for this team.");
        }

        if (rules.CycleId is { } cycleId
            && !await db.Cycles.AnyAsync(c => c.Id == cycleId && c.TeamId == teamId))
        {
            return ValidationProblem(title: "Unknown cycle for this team.");
        }

        if (rules.LabelId is { } labelId
            && !await db.Labels.AnyAsync(l => l.Id == labelId && l.TeamId == teamId))
        {
            return ValidationProblem(title: "Unknown label for this team.");
        }

        return null;
    }

    private ObjectResult PersonalDefaultProblem() =>
        this.DomainRuleProblem(
            "A personal view cannot be a team default.",
            "The default is what the whole team sees, and a personal view is visible only to its owner.");
}
