namespace Tracer.Domain.Entities;

/// <summary>
/// The three relationships that are actually *stored*.
///
/// "Blocked by" and "duplicated by" are missing on purpose: they are not
/// separate kinds of link, they are the same link seen from the other end.
/// Storing them would mean writing two rows for one fact, and two rows for one
/// fact is two rows that can disagree — delete one side, or write the pair
/// non-atomically, and the graph now says A blocks B while B is not blocked by
/// anything. See <see cref="IssueRelationKind"/> for how the other end is
/// offered to clients without ever being written down.
/// </summary>
public enum IssueRelationType
{
    /// <summary>Symmetric: if A relates to B, B relates to A. Direction carries no meaning.</summary>
    Relates = 0,

    /// <summary>Directed: source blocks target. The inverse is "blocked by".</summary>
    Blocks = 1,

    /// <summary>Directed: source duplicates target. The inverse is "duplicated by".</summary>
    Duplicates = 2,
}

/// <summary>
/// A link between two issues, stored exactly once in a canonical direction.
/// </summary>
public class IssueRelation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SourceIssueId { get; set; }
    public Issue? SourceIssue { get; set; }

    public Guid TargetIssueId { get; set; }
    public Issue? TargetIssue { get; set; }

    public IssueRelationType Type { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
