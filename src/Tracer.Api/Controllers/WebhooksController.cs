using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// Team webhook subscriptions and their delivery log.
///
/// Team-scoped like everything else a team owns, and configurable by its own
/// members: a webhook is plumbing for the team's own work, not a workspace-level
/// decision.
/// </summary>
[ApiController]
public class WebhooksController(TracerDbContext db, TeamAccess access) : ControllerBase
{
    [HttpGet("api/teams/{teamId:guid}/webhooks")]
    public async Task<ActionResult<PagedResult<WebhookDto>>> ListForTeam(Guid teamId, [FromQuery] PageQuery paging)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var webhooks = await db.Webhooks
            .Where(w => w.TeamId == teamId)
            .OrderBy(w => w.CreatedAt)
            .ThenBy(w => w.Id)
            .ToPagedResultAsync(paging, w => w.ToDto());

        return Ok(webhooks);
    }

    [HttpPost("api/teams/{teamId:guid}/webhooks")]
    public async Task<ActionResult<CreatedWebhookDto>> Create(Guid teamId, CreateWebhookRequest request)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        if (!WebhookUrlPolicy.IsAllowed(request.Url, out var reason))
        {
            return RejectedUrl(reason);
        }

        var webhook = new Webhook
        {
            TeamId = teamId,
            Name = request.Name,
            Url = request.Url,
            Events = Distinct(request.Events!),
            Secret = ApiKeyToken.Mint(),
        };
        db.Webhooks.Add(webhook);
        await db.SaveChangesAsync();

        return CreatedAtAction(
            nameof(Get),
            new { id = webhook.Id },
            new CreatedWebhookDto(
                webhook.Id,
                webhook.TeamId,
                webhook.Name,
                webhook.Url,
                webhook.Events,
                webhook.IsActive,
                webhook.Secret,
                webhook.CreatedAt));
    }

    [HttpGet("api/webhooks/{id:guid}")]
    public async Task<ActionResult<WebhookDto>> Get(Guid id)
    {
        var webhook = await FindVisibleAsync(id);
        return webhook is null ? this.NotFoundProblem("Webhook", id) : Ok(webhook.ToDto());
    }

    [HttpPut("api/webhooks/{id:guid}")]
    public async Task<ActionResult<WebhookDto>> Update(Guid id, UpdateWebhookRequest request)
    {
        var webhook = await FindVisibleAsync(id);
        if (webhook is null)
        {
            return this.NotFoundProblem("Webhook", id);
        }

        if (!WebhookUrlPolicy.IsAllowed(request.Url, out var reason))
        {
            return RejectedUrl(reason);
        }

        webhook.Name = request.Name;
        webhook.Url = request.Url;
        webhook.Events = Distinct(request.Events!);
        webhook.IsActive = request.IsActive;
        await db.SaveChangesAsync();

        return Ok(webhook.ToDto());
    }

    /// <summary>
    /// Replaces the signing secret. The old one stops working the moment this
    /// returns — which is the point of a rotation, and why the response is the
    /// only place the new one appears.
    /// </summary>
    [HttpPost("api/webhooks/{id:guid}/rotate-secret")]
    public async Task<ActionResult<WebhookSecretDto>> RotateSecret(Guid id)
    {
        var webhook = await FindVisibleAsync(id);
        if (webhook is null)
        {
            return this.NotFoundProblem("Webhook", id);
        }

        webhook.Secret = ApiKeyToken.Mint();
        await db.SaveChangesAsync();

        return Ok(new WebhookSecretDto(webhook.Id, webhook.Secret));
    }

    /// <summary>Deletes a webhook and its delivery log with it.</summary>
    [HttpDelete("api/webhooks/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var webhook = await FindVisibleAsync(id);
        if (webhook is null)
        {
            return this.NotFoundProblem("Webhook", id);
        }

        db.Webhooks.Remove(webhook);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// The delivery log: what was sent, what came back, and what is still owed.
    /// This is the endpoint a team lives in when their integration is broken, so
    /// the failure classification and attempt count are on every row.
    /// </summary>
    [HttpGet("api/webhooks/{id:guid}/deliveries")]
    public async Task<ActionResult<PagedResult<WebhookDeliveryDto>>> Deliveries(
        Guid id,
        [FromQuery] WebhookDeliveryQuery query)
    {
        if (await FindVisibleAsync(id) is null)
        {
            return this.NotFoundProblem("Webhook", id);
        }

        var deliveries = db.WebhookDeliveries.Where(d => d.WebhookId == id);

        if (query.Status is { } status)
        {
            deliveries = deliveries.Where(d => d.Status == status);
        }

        var total = await deliveries.CountAsync();

        var results = await deliveries
            .OrderByDescending(d => d.CreatedAt)
            .ThenBy(d => d.Id) // deliveries fanned out from one event share a timestamp exactly
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return Ok(new PagedResult<WebhookDeliveryDto>(
            results.Select(d => d.ToDto()).ToList(),
            query.Page,
            query.PageSize,
            total,
            (int)Math.Ceiling(total / (double)query.PageSize)));
    }

    /// <summary>One delivery, including the exact bytes that were signed and sent.</summary>
    [HttpGet("api/webhooks/{id:guid}/deliveries/{deliveryId:guid}")]
    public async Task<ActionResult<WebhookDeliveryDetailDto>> Delivery(Guid id, Guid deliveryId)
    {
        if (await FindVisibleAsync(id) is null)
        {
            return this.NotFoundProblem("Webhook", id);
        }

        var delivery = await db.WebhookDeliveries
            .SingleOrDefaultAsync(d => d.Id == deliveryId && d.WebhookId == id);

        return delivery is null
            ? this.NotFoundProblem("Delivery on this webhook", deliveryId)
            : Ok(new WebhookDeliveryDetailDto(delivery.ToDto(), delivery.Payload));
    }

    /// <summary>
    /// Queues a failed delivery to be tried again, with its attempt count reset
    /// and its original payload intact — the same bytes, so the event a consumer
    /// eventually sees still describes the moment it happened rather than now.
    /// </summary>
    [HttpPost("api/webhooks/{id:guid}/deliveries/{deliveryId:guid}/redeliver")]
    public async Task<ActionResult<WebhookDeliveryDto>> Redeliver(Guid id, Guid deliveryId)
    {
        if (await FindVisibleAsync(id) is null)
        {
            return this.NotFoundProblem("Webhook", id);
        }

        var delivery = await db.WebhookDeliveries
            .SingleOrDefaultAsync(d => d.Id == deliveryId && d.WebhookId == id);
        if (delivery is null)
        {
            return this.NotFoundProblem("Delivery on this webhook", deliveryId);
        }

        if (delivery.Status == WebhookDeliveryStatus.Pending)
        {
            return this.ConflictProblem(
                "Delivery is still pending.",
                "This delivery has not finished yet; it will be attempted again on its own.");
        }

        delivery.Status = WebhookDeliveryStatus.Pending;
        delivery.AttemptCount = 0;
        delivery.NextAttemptAt = DateTimeOffset.UtcNow;
        delivery.FailureClass = WebhookFailureClass.None;
        delivery.Error = null;
        await db.SaveChangesAsync();

        return Ok(delivery.ToDto());
    }

    private async Task<Webhook?> FindVisibleAsync(Guid id)
    {
        var webhook = await db.Webhooks.FindAsync(id);
        return webhook is not null && await access.CanAccessTeamAsync(User, webhook.TeamId) ? webhook : null;
    }

    /// <summary>
    /// A 400: the URL is well-formed but points somewhere this server will not
    /// go. The reason is returned because it is the caller's own configuration —
    /// nothing about the network is disclosed that they did not just assert.
    /// </summary>
    private ActionResult RejectedUrl(string reason) =>
        ValidationProblem(title: $"Webhook URL is not allowed. {reason}");

    /// <summary>
    /// Subscribing to the same event twice would fan out two identical deliveries
    /// per change, forever, and look like a bug in the sender rather than in the
    /// subscription.
    /// </summary>
    private static List<WebhookEvent> Distinct(List<WebhookEvent> events) =>
        events.Distinct().OrderBy(e => e).ToList();
}
