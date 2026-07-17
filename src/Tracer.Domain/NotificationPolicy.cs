using Tracer.Domain.Entities;

namespace Tracer.Domain;

/// <summary>
/// Which changes are worth a person's attention.
///
/// <para>
/// The audit log records everything, because reconstructing exactly what
/// happened is its job. An inbox is not that. It is a list of things a watcher
/// would want to <em>act on</em>, and it competes for attention with everything
/// else demanding it — so the moment it fills with "someone changed the estimate
/// from 3 to 5", people stop reading it, and then it stops working even for the
/// notifications that mattered. Curating is not throwing information away; the
/// information is all in the feed. It is refusing to page someone about it.
/// </para>
/// <para>
/// This mirrors <see cref="WebhookEvents"/>, which maps the same exhaustive log
/// onto a small public surface for the same reason: the internal record and the
/// thing you push at people are different sizes on purpose.
/// </para>
/// </summary>
public static class NotificationPolicy
{
    /// <summary>
    /// True when subscribers should be told. The field edits, label changes, and
    /// comment/relation removals are all in the audit log; none of them is a
    /// reason to interrupt someone.
    /// </summary>
    public static bool IsNotable(ActivityType type) => type switch
    {
        ActivityType.CommentCreated => true,     // someone is talking on your issue
        ActivityType.IssueStateChanged => true,  // it moved
        ActivityType.IssueAssigned => true,      // it changed hands
        ActivityType.IssueRelationAdded => true, // it is now blocked / a duplicate
        ActivityType.IssueParentChanged => true, // it was re-homed
        ActivityType.IssueDeleted => true,       // it is gone — you were watching it

        _ => false,
    };
}
