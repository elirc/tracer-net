namespace Tracer.Domain.Entities;

/// <summary>
/// What happened to an issue. One value per *domain* event, not per column
/// touched: "assigned" and "moved to In Progress" are things people did, while
/// "the Assignee column changed from null to 'ben'" is how the database found
/// out about it.
/// </summary>
public enum ActivityType
{
    IssueCreated = 0,

    /// <summary>A plain field edit; <see cref="Activity.Field"/> says which.</summary>
    IssueUpdated = 1,

    IssueStateChanged = 2,
    IssueAssigned = 3,
    IssueLabelAdded = 4,
    IssueLabelRemoved = 5,
    IssueRelationAdded = 6,
    IssueRelationRemoved = 7,
    IssueParentChanged = 8,
    IssueDeleted = 9,
    CommentCreated = 10,
    CommentUpdated = 11,
    CommentDeleted = 12,
}

/// <summary>
/// An immutable record that something happened. Append-only: there is no API to
/// edit or delete one, because an audit log you can rewrite answers the wrong
/// question.
///
/// <para>
/// <b>Why this deliberately breaks the rules the rest of the schema follows.</b>
/// </para>
/// <para>
/// <see cref="IssueId"/> and <see cref="ActorId"/> are plain Guids with no
/// foreign key. That is not an oversight. A foreign key would have to cascade —
/// and then deleting an issue would erase the record of who deleted it, which is
/// the single question an audit log exists to answer. The log outlives the rows
/// it describes; that is what makes it a log rather than a join table.
/// </para>
/// <para>
/// Because those rows can vanish, anything needed to *read* an entry back is
/// copied in at write time: <see cref="IssueTitle"/> and
/// <see cref="ActorHandle"/> are denormalized on purpose. A feed that renders
/// "(deleted issue) was deleted by (deleted user)" is not a feed. Normalizing
/// them would be correct by every rule that applies to live data and useless
/// here.
/// </para>
/// <para>
/// <see cref="TeamId"/> is the exception that proves it: it *does* carry a
/// cascading foreign key. Deleting a team is an admin nuking a whole workspace,
/// its feed included — and since the feed is only ever read per team, orphaned
/// rows would be unreachable forever while still taking up space. Keeping the
/// team means <see cref="IssueNumber"/> can be rendered as "ENG-42" from the
/// live team key, matching how identifiers are derived everywhere else rather
/// than freezing a stale copy.
/// </para>
/// </summary>
public class Activity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>The issue this happened to. No foreign key: the issue may be gone.</summary>
    public Guid IssueId { get; set; }

    /// <summary>Per-team issue number, rendered against the live team key on read.</summary>
    public int IssueNumber { get; set; }

    /// <summary>The issue's title when this happened; kept so a deleted issue still reads.</summary>
    public required string IssueTitle { get; set; }

    public ActivityType Type { get; set; }

    /// <summary>Which field changed, for <see cref="ActivityType.IssueUpdated"/>; null otherwise.</summary>
    public string? Field { get; set; }

    /// <summary>Value before the change, rendered for a human. Null means "was not set".</summary>
    public string? OldValue { get; set; }

    /// <summary>Value after the change. Null means "no longer set" — e.g. an unassignment.</summary>
    public string? NewValue { get; set; }

    /// <summary>Who did it. No foreign key: the user may have been deleted since.</summary>
    public Guid? ActorId { get; set; }

    /// <summary>The actor's handle at the time; kept so a deleted user still reads.</summary>
    public required string ActorHandle { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
