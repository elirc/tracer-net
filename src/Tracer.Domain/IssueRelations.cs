using Tracer.Domain.Entities;

namespace Tracer.Domain;

/// <summary>
/// A relationship as a *client* names it, from the point of view of the issue
/// they are looking at. Five kinds here, three in <see cref="IssueRelationType"/>:
/// the extra two are the directed relations seen from the far end.
/// </summary>
public enum IssueRelationKind
{
    Relates = 0,
    Blocks = 1,
    BlockedBy = 2,
    Duplicates = 3,
    DuplicatedBy = 4,
}

/// <summary>
/// Translates between how relations are talked about and how they are stored.
///
/// <para>
/// Clients get all five kinds, in both directions, because "TRA-9 is blocked by
/// TRA-4" is how people actually describe the situation and forcing them to
/// invert it by hand is a worse API. Storage gets three, in one direction,
/// because a fact recorded twice is a fact that can contradict itself.
/// <see cref="Canonicalize"/> is the seam between the two: asking to add
/// "9 blocked by 4" writes the single row "4 blocks 9".
/// </para>
/// </summary>
public static class IssueRelations
{
    /// <summary>
    /// Rewrites a request stated from <paramref name="subject"/>'s point of view
    /// into the exact row that will be stored. A reversed kind swaps the
    /// endpoints rather than inventing a new type.
    ///
    /// <para>
    /// Every spelling of one fact must land on one tuple, because the uniqueness
    /// of <c>(source, target, type)</c> is what actually stops a duplicate — a
    /// controller check cannot, since two concurrent requests both pass it before
    /// either writes. For the directed kinds that falls out for free: "9 blocked
    /// by 4" and "4 blocks 9" already produce the same row.
    /// </para>
    /// <para>
    /// <see cref="IssueRelationType.Relates"/> needs help. It has no direction —
    /// "A relates to B" and "B relates to A" are the same sentence — but stored
    /// naively they are two different tuples, and a unique index compares tuples,
    /// not meanings. So the endpoints are put in a fixed order first. Which order
    /// is arbitrary and carries no meaning (reads invert it away); that it is
    /// *stable* is the entire point.
    /// </para>
    /// </summary>
    public static (Guid SourceId, Guid TargetId, IssueRelationType Type) Canonicalize(
        IssueRelationKind kind,
        Guid subject,
        Guid other) => kind switch
        {
            IssueRelationKind.Relates => subject.CompareTo(other) <= 0
                ? (subject, other, IssueRelationType.Relates)
                : (other, subject, IssueRelationType.Relates),
            IssueRelationKind.Blocks => (subject, other, IssueRelationType.Blocks),
            IssueRelationKind.BlockedBy => (other, subject, IssueRelationType.Blocks),
            IssueRelationKind.Duplicates => (subject, other, IssueRelationType.Duplicates),
            IssueRelationKind.DuplicatedBy => (other, subject, IssueRelationType.Duplicates),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown relation kind."),
        };

    /// <summary>
    /// How a stored row reads to whichever issue is looking at it.
    /// <paramref name="fromSource"/> is true when the viewer is the row's source.
    /// A symmetric relation reads the same from both ends; a directed one inverts.
    /// </summary>
    public static IssueRelationKind AsSeenFrom(IssueRelationType type, bool fromSource) => type switch
    {
        IssueRelationType.Relates => IssueRelationKind.Relates,
        IssueRelationType.Blocks => fromSource ? IssueRelationKind.Blocks : IssueRelationKind.BlockedBy,
        IssueRelationType.Duplicates => fromSource ? IssueRelationKind.Duplicates : IssueRelationKind.DuplicatedBy,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown relation type."),
    };
}
