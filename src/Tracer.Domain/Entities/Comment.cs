namespace Tracer.Domain.Entities;

public class Comment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid IssueId { get; set; }
    public Issue? Issue { get; set; }

    public required string Author { get; set; }

    public required string Body { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
