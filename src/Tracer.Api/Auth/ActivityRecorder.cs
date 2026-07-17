using System.Security.Claims;
using Tracer.Api.Notifications;
using Tracer.Api.Webhooks;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Auth;

/// <summary>
/// Writes the audit trail. Every issue mutation goes through here, so the feed,
/// and everything later built on it, has exactly one source.
///
/// <para>
/// <b>It does not call SaveChanges, and that is the whole point.</b> The activity
/// is added to the same <see cref="TracerDbContext"/> the mutation is sitting in,
/// so the caller's existing <c>SaveChangesAsync</c> commits both in one
/// transaction. Recording separately — a second save, a background queue, an
/// outbox drain — buys a window where the change lands and the record does not,
/// or the reverse. An audit log that disagrees with the data is worse than none,
/// because it is trusted.
/// </para>
/// <para>
/// <b>Why controllers call this rather than a SaveChanges interceptor.</b> An
/// interceptor over the change tracker would be tamper-proof and impossible to
/// forget, which is tempting. But it sees columns, not intent: it cannot tell a
/// re-parent from an assignment without re-deriving the meaning from the diff,
/// it cannot name the actor without reaching for ambient request state, and it
/// would report a rank rewrite during a rebalance — a move of *other* people's
/// cards — as a dozen edits nobody made. The trade is real: this can be
/// forgotten. The tests are where that is caught.
/// </para>
/// </summary>
public sealed class ActivityRecorder(TracerDbContext db, WebhookOutbox webhooks, NotificationFanout notifications)
{
    /// <summary>
    /// One instant for the whole request — this is registered scoped, so it is
    /// stamped once and shared by everything recorded here.
    ///
    /// <para>
    /// Everything this recorder writes commits in a single transaction, so it all
    /// became true at the same moment; reading the clock per entry would instead
    /// record the order the C# happened to run in, spreading one atomic change
    /// across a few microseconds of invented chronology. Editing an issue's title
    /// and priority in one save did not happen twice, one after the other.
    /// </para>
    /// <para>
    /// This is why the feeds break ties on id. Entries from one save now collide
    /// on <see cref="Activity.CreatedAt"/> exactly, and paging a result set whose
    /// order is only "whatever the planner returns" silently repeats and skips
    /// rows.
    /// </para>
    /// </summary>
    private readonly DateTimeOffset _at = DateTimeOffset.UtcNow;

    /// <summary>
    /// Records a change and fans it out to everything that follows from one.
    ///
    /// This is the spine: the audit entry and any webhook deliveries are added to
    /// the same DbContext, so the caller's single <c>SaveChangesAsync</c> commits
    /// the change, the record of it, and the promise to announce it — or none of
    /// them. Fanning out anywhere else would let a webhook fire for a change that
    /// rolled back, or a change commit with nobody told.
    /// </summary>
    public async Task<Activity> RecordAsync(
        ClaimsPrincipal user,
        Issue issue,
        ActivityType type,
        string? field = null,
        string? oldValue = null,
        string? newValue = null)
    {
        var activity = new Activity
        {
            TeamId = issue.TeamId,
            IssueId = issue.Id,
            IssueNumber = issue.Number,
            IssueTitle = issue.Title,
            Type = type,
            Field = field,
            OldValue = oldValue,
            NewValue = newValue,
            ActorId = user.UserId(),
            ActorHandle = user.Handle(),
            CreatedAt = _at,
        };

        db.Activities.Add(activity);
        await webhooks.EnqueueAsync(activity, issue);
        await notifications.HandleAsync(user, activity, issue);
        return activity;
    }

    /// <summary>
    /// Records the plain field edits between an issue's previous values and its
    /// current ones. Call it after mutating the entity, with what it held before.
    ///
    /// <para>
    /// This lives here rather than in a controller because there is now more than
    /// one way to edit an issue — a PUT and a bulk import — and "what counts as a
    /// change worth logging" must not have two answers. A private copy in each
    /// caller is how a feed ends up reporting a title edit made through one route
    /// and staying silent about the identical edit made through the other.
    /// </para>
    /// </summary>
    public async Task RecordFieldEditsAsync(
        ClaimsPrincipal user,
        Issue issue,
        string? oldTitle,
        string? oldDescription,
        IssuePriority oldPriority,
        int? oldEstimate)
    {
        if (!string.Equals(oldTitle, issue.Title, StringComparison.Ordinal))
        {
            await RecordAsync(user, issue, ActivityType.IssueUpdated, "title", oldTitle, issue.Title);
        }

        if (!string.Equals(oldDescription, issue.Description, StringComparison.Ordinal))
        {
            // The values themselves are left out: a description can run to
            // kilobytes, and an audit log is not a place to store two copies of
            // it. That it changed, by whom, and when is the useful part.
            await RecordAsync(user, issue, ActivityType.IssueUpdated, "description");
        }

        if (oldPriority != issue.Priority)
        {
            await RecordAsync(user, issue, ActivityType.IssueUpdated, "priority",
                oldPriority.ToString(), issue.Priority.ToString());
        }

        if (oldEstimate != issue.Estimate)
        {
            await RecordAsync(user, issue, ActivityType.IssueUpdated, "estimate",
                oldEstimate?.ToString(), issue.Estimate?.ToString());
        }
    }
}
