namespace Tracer.Domain.Entities;

/// <summary>Who a saved view is for.</summary>
public enum SavedViewScope
{
    /// <summary>Shared with the whole team; anyone on the team can see and edit it.</summary>
    Team = 0,

    /// <summary>Private to the person who made it, and to nobody else.</summary>
    Personal = 1,
}

/// <summary>
/// A named, reusable set of issue filters.
///
/// <para>
/// <b>Ownership follows scope.</b> <see cref="OwnerUserId"/> is set for a
/// <see cref="SavedViewScope.Personal"/> view and null for a
/// <see cref="SavedViewScope.Team"/> one: a shared view is the team's property,
/// not a possession of whoever happened to create it, so it outlives their
/// account rather than being cascade-deleted out from under the team. The
/// invariant "owner is null exactly when the scope is Team" is what makes
/// "who may see this?" a question with one answer.
/// </para>
/// <para>
/// The rules live in <see cref="FilterJson"/> as JSON rather than as a column
/// per filter. Filters are a list that grows every time search learns a new
/// trick, and a column per filter turns each of those into a migration; the
/// column would also be wrong the moment a rule stops being a single scalar.
/// The cost is that the database cannot validate the rules, so the API does,
/// before they are ever written.
/// </para>
/// </summary>
public class SavedView
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    public required string Name { get; set; }

    public SavedViewScope Scope { get; set; }

    /// <summary>The owner of a personal view; null for a team view. See the class remarks.</summary>
    public Guid? OwnerUserId { get; set; }
    public User? Owner { get; set; }

    /// <summary>The view's rules: a serialized issue filter.</summary>
    public required string FilterJson { get; set; }

    /// <summary>
    /// The view a team lands on when nobody has chosen one. At most one per team,
    /// enforced by a filtered unique index rather than by hope.
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
