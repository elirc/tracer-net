namespace Tracer.Domain.Entities;

/// <summary>
/// One entry in one person's inbox: "this happened, and you were watching."
///
/// <para>
/// A notification does not copy what happened — it points at the
/// <see cref="Activity"/> that caused it. The activity is already an immutable,
/// denormalized record that survives the deletion of the issue it describes, so
/// pointing at it means an inbox item still reads correctly after the issue is
/// gone, without this row duplicating the title, actor, and before/after values
/// the activity already froze. The audit log is the source of truth for *what
/// happened*; a notification only adds *who should hear about it* and *whether
/// they have*.
/// </para>
/// </summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The recipient. A notification is inherently personal — one per subscriber per event.</summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid ActivityId { get; set; }
    public Activity? Activity { get; set; }

    /// <summary>Null while unread. A timestamp rather than a bool, so "when did they see it" is answerable.</summary>
    public DateTimeOffset? ReadAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
