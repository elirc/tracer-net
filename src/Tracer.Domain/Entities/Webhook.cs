namespace Tracer.Domain.Entities;

/// <summary>
/// What a team can subscribe to. Deliberately a short list of things that
/// *happened to the product*, not a mirror of <see cref="ActivityType"/>.
///
/// The audit log records fifteen kinds of change because a log is for
/// reconstructing exactly what occurred. A webhook consumer wants to know an
/// issue changed, not whether it was the estimate or the parent — so several
/// activity types collapse into <see cref="IssueUpdated"/>, and the ones nobody
/// can act on remotely do not fire at all. Exposing the enum one-to-one would
/// have meant every new activity type silently became a new public event,
/// permanently.
/// </summary>
public enum WebhookEvent
{
    IssueCreated = 0,
    IssueUpdated = 1,
    IssueStateChanged = 2,
    CommentCreated = 3,
}

/// <summary>A team's subscription: where to send events, and which ones.</summary>
public class Webhook
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>Human label, so a webhook is recognisable before someone disables it.</summary>
    public required string Name { get; set; }

    public required string Url { get; set; }

    /// <summary>
    /// HMAC key for signing payloads.
    ///
    /// Stored in the clear, unlike an <see cref="ApiKey"/>, and the difference is
    /// forced rather than chosen: an API key is only ever *compared*, so a hash is
    /// enough, while this one must be *used* to compute a signature on every send.
    /// A hash cannot sign. The mitigation is exposure, not storage — it is
    /// returned when created or rotated and never echoed by a read.
    /// </summary>
    public required string Secret { get; set; }

    public List<WebhookEvent> Events { get; set; } = [];

    /// <summary>
    /// Lets a team stop a noisy or broken endpoint without deleting it — and so
    /// without losing its delivery history, which is the thing they need to work
    /// out why it broke.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<WebhookDelivery> Deliveries { get; set; } = [];
}
