namespace Tracer.Domain.Entities;

public class Team
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name, e.g. "Engineering".</summary>
    public required string Name { get; set; }

    /// <summary>Short uppercase key used in issue identifiers, e.g. "ENG".</summary>
    public required string Key { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<WorkflowState> WorkflowStates { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public List<Issue> Issues { get; set; } = [];
    public List<Label> Labels { get; set; } = [];
    public List<Cycle> Cycles { get; set; } = [];
    public List<TeamMembership> Memberships { get; set; } = [];
}
