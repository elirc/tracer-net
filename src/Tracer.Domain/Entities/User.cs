namespace Tracer.Domain.Entities;

/// <summary>
/// What a user may do across the workspace as a whole.
///
/// This is deliberately not a per-team role. Team-level access is a
/// membership question — "is this person on this team?" — and lives in
/// <see cref="TeamMembership"/>. Mixing the two into one enum ("team admin",
/// "team member", "workspace admin") is how authorization checks end up
/// meaning different things in different controllers.
/// </summary>
public enum WorkspaceRole
{
    /// <summary>Belongs to specific teams and can only see those teams.</summary>
    Member = 0,

    /// <summary>Administers the workspace: sees every team, manages users and keys.</summary>
    Admin = 1,
}

/// <summary>
/// A person who can call the API.
///
/// <see cref="Handle"/> shares a namespace with <c>Issue.Assignee</c> and
/// <c>Comment.Author</c>, which remain plain strings. Those fields were never
/// foreign keys and turning them into one now would rewrite half the product
/// for no gain: an assignee is a label on an issue, not a permission grant.
/// The handle is what ties the two together.
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Short unique handle, e.g. "ana"; matches assignee and comment-author strings.</summary>
    public required string Handle { get; set; }

    public required string Name { get; set; }

    public WorkspaceRole Role { get; set; } = WorkspaceRole.Member;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ApiKey> ApiKeys { get; set; } = [];
    public List<TeamMembership> Memberships { get; set; } = [];

    /// <summary>Personal saved views. Team views have no owner, so they are not listed here.</summary>
    public List<SavedView> SavedViews { get; set; } = [];
}
