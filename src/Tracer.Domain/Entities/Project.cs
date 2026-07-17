namespace Tracer.Domain.Entities;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Issue> Issues { get; set; } = [];
}
