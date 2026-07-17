using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

public class SubIssuesApiTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public SubIssuesApiTests(TracerApiFactory factory)
    {
        _client = factory.CreateAdminClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record IssuePayload(Guid Id, Guid TeamId, string Identifier, string Title, Guid? ParentId, Guid StateId);
    private sealed record StatePayload(Guid Id, string Name, string Type, int Position);
    private sealed record RollupPayload(
        int TotalIssues,
        int ScopeIssues,
        int CompletedIssues,
        int InProgressIssues,
        int CanceledIssues,
        int ScopeEstimate,
        int CompletedEstimate,
        double ProgressPercent);
    private sealed record SubIssuesPayload(RollupPayload Rollup, List<IssuePayload> Items);

    private async Task<TeamPayload> CreateTeamAsync(string name, string key)
    {
        var created = await _client.PostAsJsonAsync("/api/teams", new { name, key });
        return (await created.Content.ReadFromJsonAsync<TeamPayload>())!;
    }

    private async Task<IssuePayload> CreateIssueAsync(Guid teamId, string title, Guid? parentId = null, int? estimate = null)
    {
        var created = await _client.PostAsJsonAsync($"/api/teams/{teamId}/issues", new { title, parentId, estimate });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    private async Task<List<StatePayload>> StatesAsync(Guid teamId) =>
        (await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{teamId}/states"))!;

    private async Task SetParentAsync(Guid issueId, Guid? parentId, string title)
    {
        var response = await _client.PutAsJsonAsync($"/api/issues/{issueId}", new { title, parentId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Walks an issue to Done through the legal Todo -> InProgress -> Done path.</summary>
    private async Task CompleteAsync(Guid teamId, Guid issueId)
    {
        var states = await StatesAsync(teamId);
        foreach (var type in new[] { "Todo", "InProgress", "Done" })
        {
            var state = states.Single(s => s.Type == type);
            var moved = await _client.PostAsJsonAsync($"/api/issues/{issueId}/transitions", new { stateId = state.Id });
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }
    }

    private async Task CancelAsync(Guid teamId, Guid issueId)
    {
        var canceled = (await StatesAsync(teamId)).Single(s => s.Type == "Canceled");
        var moved = await _client.PostAsJsonAsync($"/api/issues/{issueId}/transitions", new { stateId = canceled.Id });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
    }

    [Fact]
    public async Task An_issue_can_be_created_under_a_parent()
    {
        var team = await CreateTeamAsync("Sub A", "SBA");
        var parent = await CreateIssueAsync(team.Id, "epic");

        var child = await CreateIssueAsync(team.Id, "task", parent.Id);

        Assert.Equal(parent.Id, child.ParentId);
        var children = await _client.GetFromJsonAsync<SubIssuesPayload>($"/api/issues/{parent.Id}/children");
        Assert.Equal([child.Id], children!.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task An_existing_issue_can_be_nested_and_un_nested()
    {
        var team = await CreateTeamAsync("Sub B", "SBB");
        var parent = await CreateIssueAsync(team.Id, "epic");
        var child = await CreateIssueAsync(team.Id, "loose task");

        await SetParentAsync(child.Id, parent.Id, "loose task");
        var nested = await _client.GetFromJsonAsync<IssuePayload>($"/api/issues/{child.Id}");
        Assert.Equal(parent.Id, nested!.ParentId);

        // A PUT that omits parentId un-nests, exactly as it clears project and cycle.
        await SetParentAsync(child.Id, null, "loose task");
        var loose = await _client.GetFromJsonAsync<IssuePayload>($"/api/issues/{child.Id}");
        Assert.Null(loose!.ParentId);
    }

    [Fact]
    public async Task Sub_issues_can_nest_more_than_one_level()
    {
        var team = await CreateTeamAsync("Sub C", "SBC");
        var grandparent = await CreateIssueAsync(team.Id, "initiative");
        var parent = await CreateIssueAsync(team.Id, "epic", grandparent.Id);
        var child = await CreateIssueAsync(team.Id, "task", parent.Id);

        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal(grandparent.Id, parent.ParentId);
    }

    // ---- Cycle guards ----

    [Fact]
    public async Task An_issue_cannot_be_its_own_parent()
    {
        var team = await CreateTeamAsync("Sub D", "SBD");
        var issue = await CreateIssueAsync(team.Id, "recursive");

        var response = await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "recursive", parentId = issue.Id });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_parent_cannot_be_re_parented_under_its_own_child()
    {
        var team = await CreateTeamAsync("Sub E", "SBE");
        var parent = await CreateIssueAsync(team.Id, "epic");
        var child = await CreateIssueAsync(team.Id, "task", parent.Id);

        var response = await _client.PutAsJsonAsync($"/api/issues/{parent.Id}",
            new { title = "epic", parentId = child.Id });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_parent_cannot_be_re_parented_under_a_distant_descendant()
    {
        var team = await CreateTeamAsync("Sub F", "SBF");
        var top = await CreateIssueAsync(team.Id, "initiative");
        var middle = await CreateIssueAsync(team.Id, "epic", top.Id);
        var bottom = await CreateIssueAsync(team.Id, "task", middle.Id);

        var response = await _client.PutAsJsonAsync($"/api/issues/{top.Id}",
            new { title = "initiative", parentId = bottom.Id });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_parent_from_another_team_is_refused()
    {
        var first = await CreateTeamAsync("Sub G", "SBG");
        var second = await CreateTeamAsync("Sub H", "SBH");
        var foreignParent = await CreateIssueAsync(second.Id, "theirs");

        var created = await _client.PostAsJsonAsync($"/api/teams/{first.Id}/issues",
            new { title = "ours", parentId = foreignParent.Id });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }

    [Fact]
    public async Task An_unknown_parent_is_refused()
    {
        var team = await CreateTeamAsync("Sub I", "SBI");

        var created = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/issues",
            new { title = "orphan", parentId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }

    // ---- Deleting a parent ----

    /// <summary>
    /// Deleting an umbrella ticket must not silently delete the real work filed
    /// under it — the same call the product already makes for projects and cycles.
    /// </summary>
    [Fact]
    public async Task Deleting_a_parent_releases_its_children_rather_than_deleting_them()
    {
        var team = await CreateTeamAsync("Sub J", "SBJ");
        var parent = await CreateIssueAsync(team.Id, "epic");
        var child = await CreateIssueAsync(team.Id, "real work", parent.Id);

        var deleted = await _client.DeleteAsync($"/api/issues/{parent.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var survivor = await _client.GetFromJsonAsync<IssuePayload>($"/api/issues/{child.Id}");
        Assert.NotNull(survivor);
        Assert.Null(survivor.ParentId);
    }

    // ---- Roll-up ----

    [Fact]
    public async Task An_issue_with_no_children_rolls_up_to_zero()
    {
        var team = await CreateTeamAsync("Sub K", "SBK");
        var lonely = await CreateIssueAsync(team.Id, "no children");

        var children = await _client.GetFromJsonAsync<SubIssuesPayload>($"/api/issues/{lonely.Id}/children");

        Assert.Empty(children!.Items);
        Assert.Equal(0, children.Rollup.TotalIssues);
        Assert.Equal(0, children.Rollup.ProgressPercent);
    }

    [Fact]
    public async Task The_rollup_counts_completion_across_children()
    {
        var team = await CreateTeamAsync("Sub L", "SBL");
        var parent = await CreateIssueAsync(team.Id, "epic");
        var done = await CreateIssueAsync(team.Id, "finished", parent.Id, estimate: 3);
        await CreateIssueAsync(team.Id, "pending", parent.Id, estimate: 5);

        await CompleteAsync(team.Id, done.Id);

        var rollup = (await _client.GetFromJsonAsync<SubIssuesPayload>($"/api/issues/{parent.Id}/children"))!.Rollup;

        Assert.Equal(2, rollup.TotalIssues);
        Assert.Equal(2, rollup.ScopeIssues);
        Assert.Equal(1, rollup.CompletedIssues);
        Assert.Equal(8, rollup.ScopeEstimate);
        Assert.Equal(3, rollup.CompletedEstimate);
        Assert.Equal(50, rollup.ProgressPercent);
    }

    /// <summary>
    /// Canceled children leave the scope but stay reported — the same rule the
    /// cycle roll-up already applies. Called-off work should not count against
    /// the parent's completion, and it should not vanish either.
    /// </summary>
    [Fact]
    public async Task A_canceled_child_leaves_the_scope_but_is_still_reported()
    {
        var team = await CreateTeamAsync("Sub M", "SBM");
        var parent = await CreateIssueAsync(team.Id, "epic");
        var done = await CreateIssueAsync(team.Id, "finished", parent.Id, estimate: 2);
        var dropped = await CreateIssueAsync(team.Id, "called off", parent.Id, estimate: 8);

        await CompleteAsync(team.Id, done.Id);
        await CancelAsync(team.Id, dropped.Id);

        var rollup = (await _client.GetFromJsonAsync<SubIssuesPayload>($"/api/issues/{parent.Id}/children"))!.Rollup;

        Assert.Equal(2, rollup.TotalIssues);
        Assert.Equal(1, rollup.ScopeIssues);
        Assert.Equal(1, rollup.CanceledIssues);
        Assert.Equal(2, rollup.ScopeEstimate); // the canceled 8 points are out of scope
        // Everything still in scope is done, so the parent reads as complete.
        Assert.Equal(100, rollup.ProgressPercent);
    }

    [Fact]
    public async Task The_rollup_counts_only_direct_children()
    {
        var team = await CreateTeamAsync("Sub N", "SBN");
        var top = await CreateIssueAsync(team.Id, "initiative");
        var middle = await CreateIssueAsync(team.Id, "epic", top.Id);
        await CreateIssueAsync(team.Id, "task", middle.Id);

        var rollup = (await _client.GetFromJsonAsync<SubIssuesPayload>($"/api/issues/{top.Id}/children"))!.Rollup;

        Assert.Equal(1, rollup.TotalIssues);
    }

    [Fact]
    public async Task Children_of_an_unknown_issue_is_404()
    {
        var response = await _client.GetAsync($"/api/issues/{Guid.NewGuid()}/children");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
