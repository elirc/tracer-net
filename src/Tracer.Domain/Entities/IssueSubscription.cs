namespace Tracer.Domain.Entities;

/// <summary>How someone came to be watching an issue — informational, so a UI can explain the noise.</summary>
public enum SubscriptionReason
{
    /// <summary>Asked to watch it, explicitly.</summary>
    Manual = 0,

    /// <summary>Created it.</summary>
    Author = 1,

    /// <summary>Was assigned it.</summary>
    Assignee = 2,

    /// <summary>Commented on it.</summary>
    Commenter = 3,
}

/// <summary>
/// One person watching one issue. A subscription is the routing table for
/// notifications: fan-out delivers to exactly the users with a row here.
///
/// <para>
/// Subscribers are <see cref="User"/>s, not assignee strings. Assignment and
/// authorship are recorded as free-form handles across the product, but a
/// notification has to reach an actual account — so auto-subscribe resolves a
/// handle to a user and simply does nothing when no account owns it. Watching is
/// a thing accounts do; a handle on an issue is a label.
/// </para>
/// </summary>
public class IssueSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid IssueId { get; set; }
    public Issue? Issue { get; set; }

    public SubscriptionReason Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
