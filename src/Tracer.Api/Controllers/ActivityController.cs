using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// The audit trail: an issue's timeline, and a team's feed.
///
/// Read-only, and not by omission. There is no route to write, edit, or delete
/// an activity — entries are produced only as a side effect of the change they
/// describe, in the same transaction. A log with a DELETE endpoint answers "what
/// happened, unless someone preferred otherwise", which is not the question.
/// </summary>
[ApiController]
public class ActivityController(TracerDbContext db, TeamAccess access) : ControllerBase
{
    /// <summary>
    /// One issue's history, newest first.
    ///
    /// Reachable only while the issue is: authorization is drawn on the issue's
    /// team, and a deleted issue has no team to check. Its entries do survive —
    /// they are in the team feed, which is where "what happened to the thing that
    /// is no longer here" is answerable.
    /// </summary>
    [HttpGet("api/issues/{issueId:guid}/activity")]
    public async Task<ActionResult<PagedResult<ActivityDto>>> ForIssue(
        Guid issueId,
        [FromQuery] ActivityFeedQuery query)
    {
        var issue = await db.Issues.FindAsync(issueId);
        if (issue is null || !await access.CanAccessTeamAsync(User, issue.TeamId))
        {
            return this.NotFoundProblem("Issue", issueId);
        }

        return await PageAsync(db.Activities.Where(a => a.IssueId == issueId), query);
    }

    /// <summary>A team's whole feed, newest first, filtered.</summary>
    [HttpGet("api/teams/{teamId:guid}/activity")]
    public async Task<ActionResult<PagedResult<ActivityDto>>> ForTeam(
        Guid teamId,
        [FromQuery] ActivityFeedQuery query)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var activities = db.Activities.Where(a => a.TeamId == teamId);

        if (query.IssueId is { } issueId)
        {
            activities = activities.Where(a => a.IssueId == issueId);
        }

        if (query.Type is { } type)
        {
            activities = activities.Where(a => a.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            var actor = query.Actor.Trim().ToLower();
            activities = activities.Where(a => a.ActorHandle.ToLower() == actor);
        }

        if (query.Since is { } since)
        {
            activities = activities.Where(a => a.CreatedAt >= since);
        }

        if (query.Until is { } until)
        {
            // Exclusive, matching the half-open intervals cycles already use, so
            // consecutive windows tile without double-counting an entry.
            activities = activities.Where(a => a.CreatedAt < until);
        }

        return await PageAsync(activities, query);
    }

    /// <summary>
    /// Pages a feed newest-first, breaking ties by id.
    ///
    /// Two entries written in the same transaction — a title edit and a priority
    /// edit from one save — share a timestamp exactly. Without a tiebreak their
    /// order is whatever the query planner felt like, and paging a result set
    /// with an unstable order silently repeats and skips rows. The issue search
    /// learned this already; the rule is the same one.
    /// </summary>
    private async Task<ActionResult<PagedResult<ActivityDto>>> PageAsync(
        IQueryable<Activity> activities,
        ActivityFeedQuery query)
    {
        var total = await activities.CountAsync();

        var results = await activities
            .OrderByDescending(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .Include(a => a.Team)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return Ok(new PagedResult<ActivityDto>(
            results.Select(a => a.ToDto()).ToList(),
            query.Page,
            query.PageSize,
            total,
            (int)Math.Ceiling(total / (double)query.PageSize)));
    }
}
