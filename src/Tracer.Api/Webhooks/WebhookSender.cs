using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Webhooks;

/// <summary>
/// Drains the outbox: takes whatever deliveries are due, POSTs them, and records
/// what happened.
///
/// <para>
/// Deliberately separate from the request that caused the event. Sending inside
/// the request would hold a transaction open across a stranger's network, make
/// every write as slow as the slowest subscriber, and give a retry nowhere to
/// live — a second attempt cannot outlive the process that died during the
/// first. Here, a delivery that fails is simply still due.
/// </para>
/// </summary>
public sealed class WebhookSender(
    TracerDbContext db,
    IHttpClientFactory clients,
    ILogger<WebhookSender> logger)
{
    public const string HttpClientName = "webhooks";

    /// <summary>How many deliveries one pass will take on. Bounded so a backlog cannot become one enormous query.</summary>
    private const int BatchSize = 20;

    /// <summary>Returns the number of deliveries attempted.</summary>
    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var due = await db.WebhookDeliveries
            .Include(d => d.Webhook)
            .Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var delivery in due)
        {
            await AttemptAsync(delivery, cancellationToken);
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return due.Count;
    }

    private async Task AttemptAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var webhook = delivery.Webhook!;
        var now = DateTimeOffset.UtcNow;

        delivery.AttemptCount++;
        delivery.LastAttemptAt = now;

        int? statusCode = null;
        string? error = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url)
            {
                Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json"),
            };

            request.Headers.TryAddWithoutValidation("X-Tracer-Event", delivery.Event.WireName());
            // The delivery id, not the event id: it identifies this attempt's
            // envelope, while the body's `id` identifies the fact and is what a
            // consumer deduplicates on.
            request.Headers.TryAddWithoutValidation("X-Tracer-Delivery", delivery.Id.ToString());
            request.Headers.TryAddWithoutValidation("X-Tracer-Attempt", delivery.AttemptCount.ToString());
            request.Headers.TryAddWithoutValidation(
                WebhookSignature.HeaderName,
                WebhookSignature.Header(webhook.Secret, now, delivery.Payload));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("tracer-net-webhooks", "1.0"));

            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                error = $"Endpoint answered {statusCode}.";
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The request timed out. Distinguished from a real shutdown
            // cancellation below, which must not be recorded as the endpoint's
            // fault.
            error = "Timed out.";
        }
        catch (HttpRequestException exception)
        {
            // DNS failure, connection refused, TLS rejected: never reached the
            // endpoint, so there is no status to classify.
            error = Truncate(exception.Message);
        }

        Resolve(delivery, statusCode, error, now);
    }

    private void Resolve(WebhookDelivery delivery, int? statusCode, string? error, DateTimeOffset now)
    {
        delivery.ResponseStatusCode = statusCode;
        var failure = WebhookRetryPolicy.Classify(statusCode);

        if (failure == WebhookFailureClass.None)
        {
            delivery.Status = WebhookDeliveryStatus.Delivered;
            delivery.DeliveredAt = now;
            delivery.FailureClass = WebhookFailureClass.None;
            delivery.Error = null;
            return;
        }

        delivery.FailureClass = failure;
        delivery.Error = error;

        if (WebhookRetryPolicy.ShouldRetry(failure, delivery.AttemptCount))
        {
            delivery.Status = WebhookDeliveryStatus.Pending;
            delivery.NextAttemptAt = WebhookRetryPolicy.NextAttemptAt(now, delivery.AttemptCount);
            return;
        }

        delivery.Status = WebhookDeliveryStatus.Failed;

        // Logged at warning, not error: a customer's endpoint being wrong is not
        // this service being broken, and paging someone at 3am for it is how
        // alerts get muted.
        logger.LogWarning(
            "Webhook delivery {DeliveryId} to webhook {WebhookId} gave up after {Attempts} attempt(s): {Failure} {Error}",
            delivery.Id,
            delivery.WebhookId,
            delivery.AttemptCount,
            failure,
            delivery.Error);
    }

    private static string Truncate(string message) =>
        message.Length <= 200 ? message : message[..200];
}
