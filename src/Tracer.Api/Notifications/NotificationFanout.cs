using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Notifications;

/// <summary>
/// Turns a recorded change into inbox entries, and keeps the subscriber list up
/// to date as a side effect of people doing things.
///
/// <para>
/// Called from <c>ActivityRecorder</c>, so — like the webhook outbox — it runs
/// inside the same transaction as the change and its audit entry, and never
/// saves on its own. A notification cannot exist unless the change it announces
/// committed, and a committed change cannot leave its watchers untold.
/// </para>
/// </summary>
public sealed class NotificationFanout(TracerDbContext db)
{
    public async Task HandleAsync(ClaimsPrincipal actor, Activity activity, Issue issue)
    {
        // Auto-subscribe first, so that someone just assigned an issue is on the
        // watch list in time to be notified about that very assignment.
        await AutoSubscribeAsync(actor, activity, issue);

        if (!NotificationPolicy.IsNotable(activity.Type))
        {
            return;
        }

        var actorId = actor.UserId();
        foreach (var recipientId in await RecipientsAsync(issue.Id, actorId))
        {
            db.Notifications.Add(new Notification
            {
                UserId = recipientId,
                ActivityId = activity.Id,
                CreatedAt = activity.CreatedAt,
            });
        }
    }

    /// <summary>
    /// Everyone watching the issue, minus the actor.
    ///
    /// <para>
    /// You are never notified about your own action — the one thing you reliably
    /// already know is the thing you just did. Excluding the actor is what keeps
    /// "I commented" from pinging me about my own comment.
    /// </para>
    /// <para>
    /// The list is the union of persisted subscribers and the ones auto-subscribed
    /// a moment ago in this same unit of work. Those pending rows are not in the
    /// database yet — the caller has not saved — so they are read from the change
    /// tracker. Miss them and the assignee never hears they were assigned, which
    /// is the single most important notification the product sends.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyCollection<Guid>> RecipientsAsync(Guid issueId, Guid actorId)
    {
        var persisted = await db.IssueSubscriptions
            .Where(s => s.IssueId == issueId)
            .Select(s => s.UserId)
            .ToListAsync();

        var pending = db.ChangeTracker.Entries<IssueSubscription>()
            .Where(e => e.State == EntityState.Added && e.Entity.IssueId == issueId)
            .Select(e => e.Entity.UserId);

        return persisted.Concat(pending).Where(id => id != actorId).ToHashSet();
    }

    /// <summary>
    /// Puts the right person on the watch list for the kind of thing that just
    /// happened: the creator on creation, the commenter on a comment, the
    /// assignee on assignment.
    ///
    /// <para>
    /// The assignee is the interesting case — it is identified by a handle
    /// (<see cref="Activity.NewValue"/>), not by the actor, and the handle may
    /// belong to no account at all, since assignees are free-form strings. No
    /// account, no subscription, no error: you cannot route an inbox item to a
    /// name that is just a label.
    /// </para>
    /// </summary>
    private async Task AutoSubscribeAsync(ClaimsPrincipal actor, Activity activity, Issue issue)
    {
        switch (activity.Type)
        {
            case ActivityType.IssueCreated:
                await SubscribeAsync(actor.UserId(), issue.Id, SubscriptionReason.Author);
                break;

            case ActivityType.CommentCreated:
                await SubscribeAsync(actor.UserId(), issue.Id, SubscriptionReason.Commenter);
                break;

            case ActivityType.IssueAssigned when activity.NewValue is { } handle:
                var assignee = await db.Users
                    .Where(u => u.Handle == handle)
                    .Select(u => u.Id)
                    .SingleOrDefaultAsync();
                if (assignee != Guid.Empty)
                {
                    await SubscribeAsync(assignee, issue.Id, SubscriptionReason.Assignee);
                }

                break;
        }
    }

    /// <summary>
    /// Adds a subscription if one is not already present — persisted or pending.
    /// Idempotent: watching an issue you already watch changes nothing, and never
    /// downgrades a manual subscription to an incidental one.
    /// </summary>
    private async Task SubscribeAsync(Guid userId, Guid issueId, SubscriptionReason reason)
    {
        var alreadyPending = db.ChangeTracker.Entries<IssueSubscription>()
            .Any(e => e.State == EntityState.Added && e.Entity.UserId == userId && e.Entity.IssueId == issueId);
        if (alreadyPending)
        {
            return;
        }

        if (await db.IssueSubscriptions.AnyAsync(s => s.UserId == userId && s.IssueId == issueId))
        {
            return;
        }

        db.IssueSubscriptions.Add(new IssueSubscription { UserId = userId, IssueId = issueId, Reason = reason });
    }
}
