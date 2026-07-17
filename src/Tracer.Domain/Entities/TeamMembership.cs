namespace Tracer.Domain.Entities;

/// <summary>
/// Puts a user on a team. Membership is the whole of team-scoped access for a
/// <see cref="WorkspaceRole.Member"/>: no row, no visibility.
/// </summary>
public class TeamMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
