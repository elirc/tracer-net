namespace Tracer.Domain.Entities;

/// <summary>Where a cycle sits relative to now; derived from its dates, never stored.</summary>
public enum CycleStatus
{
    Upcoming = 0,
    Active = 1,
    Completed = 2,
}

/// <summary>Time-boxed iteration (sprint) scoped to a team.</summary>
public class Cycle
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>Per-team sequential cycle number.</summary>
    public int Number { get; set; }

    public string? Name { get; set; }

    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    public List<Issue> Issues { get; set; } = [];
}
