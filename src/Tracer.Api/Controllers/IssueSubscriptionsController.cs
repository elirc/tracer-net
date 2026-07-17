using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// Watching an issue: the manual half of what auto-subscribe does for you.
///
/// You can watch an issue you were not assigned and have not commented on, and
/// you can stop watching one you were auto-subscribed to. Reading the subscriber
/// list is team-scoped like the issue itself; changing <em>your own</em>
/// subscription only needs the issue to be visible to you.
/// </summary>
[ApiController]
public class IssueSubscriptionsController(TracerDbContext db, TeamAccess access) : ControllerBase
{
    [HttpGet("api/issues/{issueId:guid}/subscription")]
    public async Task<ActionResult<SubscriptionDto>> Mine(Guid issueId)
    {
        if (await FindVisibleIssueAsync(issueId) is null)
        {
            return this.NotFoundProblem("Issue", issueId);
        }

        var subscription = await db.IssueSubscriptions
            .SingleOrDefaultAsync(s => s.IssueId == issueId && s.UserId == User.UserId());

        return Ok(new SubscriptionDto(subscription is not null, subscription?.Reason));
    }

    /// <summary>
    /// Watch this issue. Idempotent, and it will not overwrite the reason on an
    /// existing subscription: someone auto-subscribed as the assignee who also
    /// hits "watch" stays recorded as the assignee, which is the truer story of
    /// why they are on the list.
    /// </summary>
    [HttpPut("api/issues/{issueId:guid}/subscription")]
    public async Task<ActionResult<SubscriptionDto>> Subscribe(Guid issueId)
    {
        if (await FindVisibleIssueAsync(issueId) is null)
        {
            return this.NotFoundProblem("Issue", issueId);
        }

        var existing = await db.IssueSubscriptions
            .SingleOrDefaultAsync(s => s.IssueId == issueId && s.UserId == User.UserId());
        if (existing is null)
        {
            existing = new IssueSubscription
            {
                IssueId = issueId,
                UserId = User.UserId(),
                Reason = SubscriptionReason.Manual,
            };
            db.IssueSubscriptions.Add(existing);
            await db.SaveChangesAsync();
        }

        return Ok(new SubscriptionDto(true, existing.Reason));
    }

    /// <summary>
    /// Stop watching. Idempotent.
    ///
    /// <para>
    /// One honest limitation: a later comment or assignment will auto-subscribe
    /// you again, because auto-subscribe cannot tell "never wanted this" from
    /// "have not been added yet" — both are the absence of a row. A durable mute
    /// would need a second kind of row that says "no", and that is a deliberate
    /// feature rather than something to smuggle in here.
    /// </para>
    /// </summary>
    [HttpDelete("api/issues/{issueId:guid}/subscription")]
    public async Task<IActionResult> Unsubscribe(Guid issueId)
    {
        if (await FindVisibleIssueAsync(issueId) is null)
        {
            return this.NotFoundProblem("Issue", issueId);
        }

        var subscription = await db.IssueSubscriptions
            .SingleOrDefaultAsync(s => s.IssueId == issueId && s.UserId == User.UserId());
        if (subscription is not null)
        {
            db.IssueSubscriptions.Remove(subscription);
            await db.SaveChangesAsync();
        }

        return NoContent();
    }

    /// <summary>Everyone watching this issue. Visible to anyone who can see the issue.</summary>
    [HttpGet("api/issues/{issueId:guid}/subscribers")]
    public async Task<ActionResult<PagedResult<IssueSubscriberDto>>> Subscribers(Guid issueId, [FromQuery] PageQuery paging)
    {
        if (await FindVisibleIssueAsync(issueId) is null)
        {
            return this.NotFoundProblem("Issue", issueId);
        }

        var subscribers = await db.IssueSubscriptions
            .Where(s => s.IssueId == issueId)
            .OrderBy(s => s.User!.Handle)
            .ThenBy(s => s.UserId)
            .Select(s => new IssueSubscriberDto(s.UserId, s.User!.Handle, s.User.Name, s.Reason))
            .ToPagedResultAsync(paging);

        return Ok(subscribers);
    }

    private async Task<Issue?> FindVisibleIssueAsync(Guid issueId)
    {
        var issue = await db.Issues.FindAsync(issueId);
        return issue is not null && await access.CanAccessTeamAsync(User, issue.TeamId) ? issue : null;
    }
}
