namespace Tracer.Domain.Entities;

/// <summary>
/// A dated target on a project's roadmap — "ship the public API by March" — that
/// issues are gathered under so progress toward it can be read off at a glance.
///
/// <para>
/// A milestone belongs to exactly one project and, through it, one team. It
/// carries <see cref="TeamId"/> denormalized alongside <see cref="ProjectId"/>
/// for the same reason an issue does: every read is team-scoped, and scoping on a
/// column the milestone already has beats joining through the project on the one
/// query — the roadmap — that is asked most.
/// </para>
/// </summary>
public class Milestone
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// The date the milestone is meant to land on. A target, not a deadline the
    /// schema enforces: the roadmap reports a milestone as overdue rather than
    /// forbidding it, because a slipped date is a fact to surface, not an error to
    /// reject.
    /// </summary>
    public DateTimeOffset TargetDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Issue> Issues { get; set; } = [];
}
