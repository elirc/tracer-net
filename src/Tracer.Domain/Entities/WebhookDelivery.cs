namespace Tracer.Domain.Entities;

public enum WebhookDeliveryStatus
{
    /// <summary>Waiting for its first attempt, or for a retry once <see cref="WebhookDelivery.NextAttemptAt"/> passes.</summary>
    Pending = 0,

    /// <summary>The endpoint answered 2xx.</summary>
    Delivered = 1,

    /// <summary>Given up on: either permanently rejected, or out of attempts.</summary>
    Failed = 2,
}

/// <summary>
/// Why an attempt failed, which decides whether trying again is worth anything.
/// </summary>
public enum WebhookFailureClass
{
    None = 0,

    /// <summary>
    /// The endpoint might succeed later: a 5xx, a timeout, a refused connection,
    /// a 429. Worth retrying — this is the deploy-window case.
    /// </summary>
    Transient = 1,

    /// <summary>
    /// The endpoint understood and refused: 400, 401, 403, 404, 410. Retrying a
    /// 404 a thousand times will not conjure the route into existence; it just
    /// turns one team's misconfiguration into a bill everyone pays.
    /// </summary>
    Permanent = 2,
}

/// <summary>
/// One event queued for one webhook — and the transactional outbox that makes
/// the whole thing honest.
///
/// <para>
/// The row is written in the same transaction as the change it announces, then
/// sent afterwards by a worker. That order is the point. Sending inside the
/// transaction would hold a database write open across somebody else's network,
/// and would announce changes that then rolled back. Sending without a row at
/// all — firing an HTTP call at the end of the request — loses the event
/// entirely if the process dies, and no amount of retrying inside that request
/// helps, because the retry dies with it.
/// </para>
/// </summary>
public class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WebhookId { get; set; }
    public Webhook? Webhook { get; set; }

    public WebhookEvent Event { get; set; }

    /// <summary>
    /// The exact bytes to send, rendered once when the event happened.
    ///
    /// Frozen deliberately. Re-rendering at send time would describe the issue as
    /// it is *now*, so a retry after an edit would deliver a payload that never
    /// corresponded to the event it claims to be — and the signature, computed
    /// over these bytes, would not match them.
    /// </summary>
    public required string Payload { get; set; }

    public WebhookDeliveryStatus Status { get; set; } = WebhookDeliveryStatus.Pending;

    public int AttemptCount { get; set; }

    /// <summary>When the next attempt becomes due; the worker picks up whatever is owed.</summary>
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>HTTP status of the last attempt; null when the request never got an answer.</summary>
    public int? ResponseStatusCode { get; set; }

    public WebhookFailureClass FailureClass { get; set; } = WebhookFailureClass.None;

    /// <summary>Short description of the last failure, for the team debugging their endpoint.</summary>
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
