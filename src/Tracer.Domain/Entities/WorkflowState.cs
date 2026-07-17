namespace Tracer.Domain.Entities;

/// <summary>
/// Broad category of a workflow state. Categories drive transition
/// validation; the states themselves are customizable per team.
/// </summary>
public enum WorkflowStateType
{
    Backlog = 0,
    Todo = 1,
    InProgress = 2,
    Done = 3,
    Canceled = 4,
}

public class WorkflowState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    public required string Name { get; set; }

    public WorkflowStateType Type { get; set; }

    /// <summary>Sort order of the state within its team's workflow.</summary>
    public int Position { get; set; }

    /// <summary>Hex color used by clients, e.g. "#95a2b3".</summary>
    public string Color { get; set; } = "#95a2b3";

    public List<Issue> Issues { get; set; } = [];
}
