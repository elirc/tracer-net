using Tracer.Domain.Entities;

namespace Tracer.Domain;

/// <summary>
/// The seam between the audit log and the public event stream.
///
/// <para>
/// Many activity types map onto one event, and several map onto none. That is
/// the point of having a mapping at all rather than publishing
/// <see cref="ActivityType"/> directly: the log is internal and can grow a new
/// value whenever the product grows a new kind of change, while every webhook
/// event is a promise to strangers that is very hard to withdraw. Wiring them
/// together would mean any new activity type instantly became a permanent public
/// API, decided by whoever added it.
/// </para>
/// </summary>
public static class WebhookEvents
{
    /// <summary>
    /// The event a change announces, or null when it announces nothing.
    ///
    /// Deletions and comment edits are unmapped on purpose: they are recorded for
    /// the audit trail, but there is no <c>issue.deleted</c> in this API's
    /// contract, and inventing one here — rather than as a deliberate addition
    /// with docs and a subscription option — is how event streams end up with
    /// events nobody designed.
    /// </summary>
    public static WebhookEvent? For(ActivityType type) => type switch
    {
        ActivityType.IssueCreated => WebhookEvent.IssueCreated,
        ActivityType.IssueStateChanged => WebhookEvent.IssueStateChanged,
        ActivityType.CommentCreated => WebhookEvent.CommentCreated,

        // Everything that is "the issue is not as it was" collapses here. A
        // consumer wants to know an issue changed and re-read it; it does not
        // want a separate event type per field, and we do not want to owe it one
        // forever.
        ActivityType.IssueUpdated
            or ActivityType.IssueAssigned
            or ActivityType.IssueLabelAdded
            or ActivityType.IssueLabelRemoved
            or ActivityType.IssueRelationAdded
            or ActivityType.IssueRelationRemoved
            or ActivityType.IssueParentChanged => WebhookEvent.IssueUpdated,

        _ => null,
    };

    /// <summary>The name on the wire, e.g. <c>issue.state_changed</c>.</summary>
    public static string WireName(this WebhookEvent value) => value switch
    {
        WebhookEvent.IssueCreated => "issue.created",
        WebhookEvent.IssueUpdated => "issue.updated",
        WebhookEvent.IssueStateChanged => "issue.state_changed",
        WebhookEvent.CommentCreated => "comment.created",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown webhook event."),
    };
}
