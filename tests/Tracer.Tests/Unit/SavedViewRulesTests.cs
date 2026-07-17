using Tracer.Domain;
using Tracer.Domain.Entities;

namespace Tracer.Tests.Unit;

public class SavedViewRulesTests
{
    private static readonly Guid Caller = Guid.NewGuid();
    private static readonly Guid Someone = Guid.NewGuid();

    [Fact]
    public void A_personal_view_is_owned_by_its_creator() =>
        Assert.Equal(Caller, SavedViewRules.OwnerFor(SavedViewScope.Personal, Caller));

    [Fact]
    public void A_team_view_has_no_owner() =>
        // Not "owned by the creator, shared with the team": a team view is the
        // team's, so it has no user to be cascade-deleted along with.
        Assert.Null(SavedViewRules.OwnerFor(SavedViewScope.Team, Caller));

    [Theory]
    [InlineData(SavedViewScope.Team, true)]
    [InlineData(SavedViewScope.Personal, false)]
    public void Only_a_team_view_can_be_a_default(SavedViewScope scope, bool expected) =>
        Assert.Equal(expected, SavedViewRules.CanBeDefault(scope));

    [Fact]
    public void A_team_view_is_visible_to_anyone_who_reaches_the_team() =>
        Assert.True(SavedViewRules.CanSee(SavedViewScope.Team, ownerUserId: null, Someone));

    [Fact]
    public void A_personal_view_is_visible_to_its_owner() =>
        Assert.True(SavedViewRules.CanSee(SavedViewScope.Personal, Caller, Caller));

    [Fact]
    public void A_personal_view_is_invisible_to_everyone_else() =>
        Assert.False(SavedViewRules.CanSee(SavedViewScope.Personal, Caller, Someone));
}
