namespace Tracer.Domain.Entities;

public class Label
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    public required string Name { get; set; }

    public string Color { get; set; } = "#5e6ad2";

    public List<Issue> Issues { get; set; } = [];
}
