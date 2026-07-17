using Tracer.Domain.Entities;

namespace Tracer.Domain;

/// <summary>
/// The rules that tie a saved view's scope to its owner, its visibility, and
/// its eligibility to be a team default.
///
/// All three follow from scope, so they are decided here rather than in the
/// controller: "personal means owned by me" and "personal means visible only to
/// me" are the same fact, and a handler that re-derives one of them by hand is
/// how the two drift apart.
/// </summary>
public static class SavedViewRules
{
    /// <summary>
    /// A personal view is owned by its creator; a team view is owned by the team,
    /// which is not a user, so it has no owner row to point at.
    /// </summary>
    public static Guid? OwnerFor(SavedViewScope scope, Guid callerId) =>
        scope == SavedViewScope.Personal ? callerId : null;

    /// <summary>
    /// Only a team view can be a team's default. A default is what everyone on the
    /// team sees when they have not picked a view, so a personal one — which by
    /// definition nobody else can see — would leave the rest of the team staring
    /// at a view that does not exist for them.
    /// </summary>
    public static bool CanBeDefault(SavedViewScope scope) => scope == SavedViewScope.Team;

    /// <summary>
    /// Whether a caller who already has access to the view's team may see it.
    ///
    /// Personal means personal: an admin reaches every team, but a workspace role
    /// is a licence to administer the workspace, not to read over someone's
    /// shoulder. A personal view holds no team data anyone is being denied — it
    /// is a bookmark, and its rules are visible to its owner alone.
    /// </summary>
    public static bool CanSee(SavedViewScope scope, Guid? ownerUserId, Guid callerId) =>
        scope == SavedViewScope.Team || ownerUserId == callerId;
}
