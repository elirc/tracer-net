namespace Tracer.Domain.Entities;

public enum IssuePriority
{
    None = 0,
    Urgent = 1,
    High = 2,
    Medium = 3,
    Low = 4,
}

public class Issue
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>Per-team sequential number; combined with the team key it forms e.g. "ENG-42".</summary>
    public int Number { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public IssuePriority Priority { get; set; } = IssuePriority.None;

    /// <summary>Story-point estimate; null when unestimated.</summary>
    public int? Estimate { get; set; }

    /// <summary>Free-form handle of the assignee; null when unassigned.</summary>
    public string? Assignee { get; set; }

    public Guid StateId { get; set; }
    public WorkflowState? State { get; set; }

    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    public Guid? CycleId { get; set; }
    public Cycle? Cycle { get; set; }

    /// <summary>The roadmap milestone this issue counts toward; null when it is on no roadmap.</summary>
    public Guid? MilestoneId { get; set; }
    public Milestone? Milestone { get; set; }

    /// <summary>
    /// The issue this one is a sub-issue of; null when it stands on its own.
    /// A parent is always in the same team — see <c>IssuesController</c> — so the
    /// hierarchy never crosses the boundary that authorization is drawn on.
    /// </summary>
    public Guid? ParentId { get; set; }
    public Issue? Parent { get; set; }
    public List<Issue> Children { get; set; } = [];

    /// <summary>
    /// This issue's identity in whatever system it was imported from; null for an
    /// issue that was created here.
    ///
    /// <para>
    /// It is what makes importing the same payload twice update rather than
    /// duplicate. Unique per team — an external id names one issue or none — but
    /// only per team: two teams importing from two different trackers may both
    /// receive an issue called "PROJ-1", and they are not the same issue.
    /// </para>
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>Fractional rank used to order issues within a state column.</summary>
    public double Position { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Label> Labels { get; set; } = [];
    public List<Comment> Comments { get; set; } = [];

    /// <summary>Relations where this issue is the stored source ("this blocks that").</summary>
    public List<IssueRelation> OutgoingRelations { get; set; } = [];

    /// <summary>Relations where this issue is the stored target ("that blocks this").</summary>
    public List<IssueRelation> IncomingRelations { get; set; } = [];
}
