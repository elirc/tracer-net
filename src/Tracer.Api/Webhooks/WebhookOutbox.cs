using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Webhooks;

/// <summary>
/// Turns a recorded activity into queued deliveries.
///
/// <para>
/// Called from <c>ActivityRecorder</c> rather than from each controller, so that
/// "webhooks fire from the activity spine" is structural instead of a convention
/// somebody has to remember. There is no way to record a change without this
/// running, and no way to fire an event that is not a recorded change.
/// </para>
/// <para>
/// Like the recorder, it never calls <c>SaveChanges</c>: the delivery rows join
/// the same transaction as the change and its audit entry. That is what makes
/// this an outbox rather than a queue — the event cannot exist unless the change
/// committed, and cannot be lost if it did.
/// </para>
/// </summary>
public sealed class WebhookOutbox(TracerDbContext db)
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public async Task EnqueueAsync(Activity activity, Issue issue)
    {
        if (WebhookEvents.For(activity.Type) is not { } wanted)
        {
            return; // nothing subscribable happened
        }

        // One narrow query on the common path: almost every team has no webhooks,
        // and this is on every mutation the product makes.
        var webhooks = await db.Webhooks
            .Where(w => w.TeamId == activity.TeamId && w.IsActive)
            .ToListAsync();

        var subscribers = webhooks.Where(w => w.Events.Contains(wanted)).ToList();
        if (subscribers.Count == 0)
        {
            return;
        }

        var payload = await RenderAsync(activity, issue, wanted);

        foreach (var webhook in subscribers)
        {
            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                WebhookId = webhook.Id,
                Event = wanted,
                Payload = payload,
                // Due immediately; the worker picks it up once the transaction
                // this is sitting in has actually committed.
                NextAttemptAt = activity.CreatedAt,
                CreatedAt = activity.CreatedAt,
            });
        }
    }

    /// <summary>
    /// Renders the payload once, now, for every subscriber to share.
    ///
    /// <para>
    /// The delivery id is deliberately *not* in here — it goes in a header. If it
    /// were in the body, two teams subscribed to one event would need two
    /// different payloads and two signatures for the same fact. What is in the
    /// body is the activity id, as <c>id</c>: it is stable across retries and
    /// identical across subscribers, which is exactly what a consumer needs to
    /// deduplicate. At-least-once delivery is a promise to send; it is not a
    /// promise to send once.
    /// </para>
    /// </summary>
    private async Task<string> RenderAsync(Activity activity, Issue issue, WebhookEvent wanted)
    {
        // The issue's own fields are read straight off the entity, and only the
        // two *names* are looked up.
        //
        // Re-querying the issue here does not work, and fails in the worst
        // possible place: on issue.created the row has been Added but not yet
        // saved — that is the whole point of the outbox — so a query against the
        // database finds nothing and the most important event in the product
        // ships with a null state and an identifier of "-1". The entity in hand
        // already has Title, Number, Priority and Assignee; the team key and
        // state name are the only things it may not have loaded, and both are
        // rows that were committed long before this request.
        var teamKey = await db.Teams
            .Where(t => t.Id == issue.TeamId)
            .Select(t => t.Key)
            .SingleOrDefaultAsync();

        var stateName = await db.WorkflowStates
            .Where(s => s.Id == issue.StateId)
            .Select(s => s.Name)
            .SingleOrDefaultAsync();

        var payload = new
        {
            // The activity id: same across retries and across subscribers, so a
            // consumer can dedupe on it.
            id = activity.Id,
            @event = wanted.WireName(),
            createdAt = activity.CreatedAt,
            actor = activity.ActorHandle,
            team = new { id = activity.TeamId, key = teamKey },
            issue = new
            {
                id = activity.IssueId,
                identifier = $"{teamKey}-{issue.Number}",
                title = issue.Title,
                state = stateName,
                priority = issue.Priority.ToString(),
                assignee = issue.Assignee,
            },
            change = new
            {
                type = activity.Type.ToString(),
                field = activity.Field,
                from = activity.OldValue,
                to = activity.NewValue,
            },
        };

        return JsonSerializer.Serialize(payload, PayloadOptions);
    }
}
