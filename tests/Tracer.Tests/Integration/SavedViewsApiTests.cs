using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// Saved views. The point of interest is that a view is rules, not results: the
/// tests here mostly assert that executing one behaves exactly like the search
/// it delegates to, and that a view's team is the only team it can ever read.
/// </summary>
public class SavedViewsApiTests : IClassFixture<TracerApiFactory>
{
    private readonly TracerApiFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _member;
    private readonly HttpClient _foreigner;

    public SavedViewsApiTests(TracerApiFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateAdminClient();
        _member = factory.CreateEngMemberClient();
        _foreigner = factory.CreateDesMemberClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record StatePayload(Guid Id, string Name, string Type, int Position);
    private sealed record LabelPayload(Guid Id, string Name, string Color);
    private sealed record IssuePayload(Guid Id, Guid TeamId, string Identifier, string Title, string? Assignee);
    private sealed record RulesPayload(
        Guid? ProjectId, Guid? StateId, Guid? CycleId, Guid? LabelId,
        string? Assignee, string? Priority, string? Q, string Sort, string Order);
    private sealed record ViewPayload(
        Guid Id, Guid TeamId, string Name, string Scope, string? Owner, bool IsDefault,
        RulesPayload Rules, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record PagePayload(List<IssuePayload> Items, int Page, int PageSize, int Total, int TotalPages);

    private async Task<TeamPayload> CreateTeamAsync(string name, string key)
    {
        var response = await _admin.PostAsJsonAsync("/api/teams", new { name, key });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TeamPayload>())!;
    }

    private async Task<TeamPayload> SeedTeamAsync(string key)
    {
        var teams = await _admin.GetFromJsonAsync<List<TeamPayload>>("/api/teams");
        return teams!.Single(t => t.Key == key);
    }

    private async Task<IssuePayload> CreateIssueAsync(HttpClient client, Guid teamId, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/teams/{teamId}/issues", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    private async Task<ViewPayload> CreateViewAsync(HttpClient client, Guid teamId, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/teams/{teamId}/views", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ViewPayload>())!;
    }

    private async Task<PagePayload> ExecuteAsync(HttpClient client, Guid viewId, string query = "")
    {
        var response = await client.GetAsync($"/api/views/{viewId}/issues{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PagePayload>())!;
    }

    // ---- Storing rules ----

    [Fact]
    public async Task A_view_round_trips_its_rules_as_an_object()
    {
        var eng = await SeedTeamAsync("ENG");

        var view = await CreateViewAsync(_member, eng.Id, new
        {
            name = "Ana's urgent work",
            scope = "Team",
            rules = new { assignee = "ana", priority = "High", sort = "Title", order = "Asc" },
        });

        Assert.Equal("Ana's urgent work", view.Name);
        Assert.Equal("Team", view.Scope);
        // Rules come back structured, not as a JSON string the client must parse again.
        Assert.Equal("ana", view.Rules.Assignee);
        Assert.Equal("High", view.Rules.Priority);
        Assert.Equal("Title", view.Rules.Sort);
        Assert.Equal("Asc", view.Rules.Order);
    }

    [Fact]
    public async Task A_view_with_no_rules_matches_everything_in_its_team()
    {
        var team = await CreateTeamAsync("View All", "SVA");
        await CreateIssueAsync(_admin, team.Id, new { title = "first" });
        await CreateIssueAsync(_admin, team.Id, new { title = "second" });

        var view = await CreateViewAsync(_admin, team.Id, new { name = "Everything" });
        var page = await ExecuteAsync(_admin, view.Id);

        Assert.Equal(2, page.Total);
    }

    // ---- Executing ----

    [Fact]
    public async Task Executing_a_view_returns_the_issues_its_rules_select()
    {
        var team = await CreateTeamAsync("View Exec", "SVE");
        await CreateIssueAsync(_admin, team.Id, new { title = "mine", assignee = "ana" });
        await CreateIssueAsync(_admin, team.Id, new { title = "theirs", assignee = "ben" });

        var view = await CreateViewAsync(_admin, team.Id, new
        {
            name = "Ana's",
            rules = new { assignee = "ana" },
        });
        var page = await ExecuteAsync(_admin, view.Id);

        Assert.Equal("mine", Assert.Single(page.Items).Title);
    }

    [Fact]
    public async Task Executing_a_view_honours_the_sort_stored_in_its_rules()
    {
        var team = await CreateTeamAsync("View Sort", "SVS");
        foreach (var title in new[] { "cherry", "apple", "banana" })
        {
            await CreateIssueAsync(_admin, team.Id, new { title });
        }

        var view = await CreateViewAsync(_admin, team.Id, new
        {
            name = "Alphabetical",
            rules = new { sort = "Title", order = "Asc" },
        });
        var page = await ExecuteAsync(_admin, view.Id);

        Assert.Equal(["apple", "banana", "cherry"], page.Items.Select(i => i.Title).ToArray());
    }

    /// <summary>
    /// The escaping search does is not re-implemented for views — it is the same
    /// code. A view whose rules are a bare "%" must match the literal character,
    /// not every issue in the team.
    /// </summary>
    [Fact]
    public async Task Executing_a_view_treats_wildcards_in_its_rules_as_literal_characters()
    {
        var team = await CreateTeamAsync("View Wildcard", "SVW");
        await CreateIssueAsync(_admin, team.Id, new { title = "no wildcard here" });
        await CreateIssueAsync(_admin, team.Id, new { title = "literally 50% off" });

        var wildcard = await CreateViewAsync(_admin, team.Id, new { name = "Percent", rules = new { q = "%" } });
        var literal = await CreateViewAsync(_admin, team.Id, new { name = "Fifty", rules = new { q = "50% off" } });

        Assert.Equal("literally 50% off", Assert.Single((await ExecuteAsync(_admin, wildcard.Id)).Items).Title);
        Assert.Equal("literally 50% off", Assert.Single((await ExecuteAsync(_admin, literal.Id)).Items).Title);
    }

    [Fact]
    public async Task Executing_a_view_pages_like_search_does()
    {
        var team = await CreateTeamAsync("View Paging", "SVP");
        for (var i = 0; i < 5; i++)
        {
            await CreateIssueAsync(_admin, team.Id, new { title = "same title" });
        }

        var view = await CreateViewAsync(_admin, team.Id, new { name = "All", rules = new { sort = "Title", order = "Asc" } });

        var seen = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await ExecuteAsync(_admin, view.Id, $"?page={page}&pageSize=2");
            Assert.Equal(5, result.Total);
            Assert.Equal(3, result.TotalPages);
            seen.AddRange(result.Items.Select(i => i.Id));
        }

        Assert.Equal(5, seen.Distinct().Count());
    }

    /// <summary>
    /// A view's team is not one of its rules, so no rule can point it at another
    /// team. A client that sends one anyway is sending a field that does not
    /// exist, and the view stays where it was created.
    /// </summary>
    [Fact]
    public async Task A_view_cannot_be_pointed_at_another_teams_issues()
    {
        var mine = await CreateTeamAsync("View Scope A", "SCA");
        var other = await CreateTeamAsync("View Scope B", "SCB");
        await CreateIssueAsync(_admin, mine.Id, new { title = "mine" });
        await CreateIssueAsync(_admin, other.Id, new { title = "theirs" });

        var view = await CreateViewAsync(_admin, mine.Id, new
        {
            name = "Sneaky",
            rules = new { teamId = other.Id },
        });
        var page = await ExecuteAsync(_admin, view.Id);

        Assert.Equal("mine", Assert.Single(page.Items).Title);
    }

    // ---- Rule validation ----

    [Fact]
    public async Task Rules_that_point_at_another_teams_label_are_rejected()
    {
        var eng = await SeedTeamAsync("ENG");
        var team = await CreateTeamAsync("View Foreign Label", "SVL");
        var engLabels = await _admin.GetFromJsonAsync<List<LabelPayload>>($"/api/teams/{eng.Id}/labels");

        var response = await _admin.PostAsJsonAsync($"/api/teams/{team.Id}/views", new
        {
            name = "Bugs",
            rules = new { labelId = engLabels!.First().Id },
        });

        // Saved as-is this view would match nothing, forever, with no explanation.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rules_that_point_at_another_teams_state_are_rejected()
    {
        var eng = await SeedTeamAsync("ENG");
        var team = await CreateTeamAsync("View Foreign State", "SVT");
        var engStates = await _admin.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{eng.Id}/states");

        var response = await _admin.PostAsJsonAsync($"/api/teams/{team.Id}/views", new
        {
            name = "Done",
            rules = new { stateId = engStates!.First().Id },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Scope and visibility ----

    [Fact]
    public async Task A_personal_view_is_invisible_to_everyone_else_including_an_admin()
    {
        var eng = await SeedTeamAsync("ENG");

        var view = await CreateViewAsync(_member, eng.Id, new { name = "Ben's private view", scope = "Personal" });

        // ana is a workspace admin and reaches every team — but a personal view
        // is a bookmark, not team data, so administering the workspace does not
        // include reading it.
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/api/views/{view.Id}")).StatusCode);

        var listed = await _admin.GetFromJsonAsync<List<ViewPayload>>($"/api/teams/{eng.Id}/views");
        Assert.DoesNotContain(listed!, v => v.Id == view.Id);
    }

    [Fact]
    public async Task A_personal_view_is_visible_to_its_owner()
    {
        var eng = await SeedTeamAsync("ENG");

        var view = await CreateViewAsync(_member, eng.Id, new { name = "Ben's own view", scope = "Personal" });

        var fetched = await _member.GetFromJsonAsync<ViewPayload>($"/api/views/{view.Id}");
        Assert.Equal("ben", fetched!.Owner);
        Assert.Equal("Personal", fetched.Scope);

        var listed = await _member.GetFromJsonAsync<List<ViewPayload>>($"/api/teams/{eng.Id}/views");
        Assert.Contains(listed!, v => v.Id == view.Id);
    }

    [Fact]
    public async Task A_team_view_is_visible_to_the_rest_of_the_team_and_has_no_owner()
    {
        var eng = await SeedTeamAsync("ENG");

        var view = await CreateViewAsync(_member, eng.Id, new { name = "Shared by ben", scope = "Team" });

        var fetched = await _admin.GetFromJsonAsync<ViewPayload>($"/api/views/{view.Id}");
        // The team owns it; ben merely typed it.
        Assert.Null(fetched!.Owner);
    }

    [Fact]
    public async Task A_view_on_a_foreign_team_is_404()
    {
        var eng = await SeedTeamAsync("ENG");
        var view = await CreateViewAsync(_admin, eng.Id, new { name = "Engineering only" });

        Assert.Equal(HttpStatusCode.NotFound, (await _foreigner.GetAsync($"/api/views/{view.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _foreigner.GetAsync($"/api/views/{view.Id}/issues")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _foreigner.GetAsync($"/api/teams/{eng.Id}/views")).StatusCode);
    }

    [Fact]
    public async Task Creating_a_view_on_a_foreign_team_is_404()
    {
        var eng = await SeedTeamAsync("ENG");

        var response = await _foreigner.PostAsJsonAsync($"/api/teams/{eng.Id}/views", new { name = "Nosy" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Views_require_a_credential()
    {
        var eng = await SeedTeamAsync("ENG");
        var anonymous = _factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/teams/{eng.Id}/views")).StatusCode);
    }

    // ---- Defaults ----

    [Fact]
    public async Task Setting_a_default_demotes_the_previous_one()
    {
        var team = await CreateTeamAsync("View Default", "SVD");

        var first = await CreateViewAsync(_admin, team.Id, new { name = "First", isDefault = true });
        Assert.True(first.IsDefault);
        Assert.Equal(first.Id, (await _admin.GetFromJsonAsync<ViewPayload>($"/api/teams/{team.Id}/views/default"))!.Id);

        var second = await CreateViewAsync(_admin, team.Id, new { name = "Second", isDefault = true });

        // One default per team: promoting the second must retire the first
        // rather than collide with it.
        Assert.True(second.IsDefault);
        Assert.Equal(second.Id, (await _admin.GetFromJsonAsync<ViewPayload>($"/api/teams/{team.Id}/views/default"))!.Id);
        Assert.False((await _admin.GetFromJsonAsync<ViewPayload>($"/api/views/{first.Id}"))!.IsDefault);

        var all = await _admin.GetFromJsonAsync<List<ViewPayload>>($"/api/teams/{team.Id}/views");
        Assert.Single(all!.Where(v => v.IsDefault));
    }

    [Fact]
    public async Task Promoting_a_view_through_an_update_also_demotes_the_previous_default()
    {
        var team = await CreateTeamAsync("View Default Update", "SVU");
        var first = await CreateViewAsync(_admin, team.Id, new { name = "First", isDefault = true });
        var second = await CreateViewAsync(_admin, team.Id, new { name = "Second" });

        var response = await _admin.PutAsJsonAsync($"/api/views/{second.Id}", new
        {
            name = "Second",
            scope = "Team",
            isDefault = true,
        });
        response.EnsureSuccessStatusCode();

        Assert.Equal(second.Id, (await _admin.GetFromJsonAsync<ViewPayload>($"/api/teams/{team.Id}/views/default"))!.Id);
        Assert.False((await _admin.GetFromJsonAsync<ViewPayload>($"/api/views/{first.Id}"))!.IsDefault);
    }

    [Fact]
    public async Task A_personal_view_cannot_be_a_team_default()
    {
        var team = await CreateTeamAsync("View Personal Default", "SPD");

        var response = await _admin.PostAsJsonAsync($"/api/teams/{team.Id}/views", new
        {
            name = "Mine only",
            scope = "Personal",
            isDefault = true,
        });

        // Well-formed, coherent, and forbidden by a domain rule: the team would
        // default to a view nobody but the owner can see.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_team_without_a_default_says_so()
    {
        var team = await CreateTeamAsync("View No Default", "SND");

        var response = await _admin.GetAsync($"/api/teams/{team.Id}/views/default");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_default_view_leads_the_list()
    {
        var team = await CreateTeamAsync("View Default Order", "SDO");
        await CreateViewAsync(_admin, team.Id, new { name = "Aardvark" });
        var chosen = await CreateViewAsync(_admin, team.Id, new { name = "Zebra", isDefault = true });

        var views = await _admin.GetFromJsonAsync<List<ViewPayload>>($"/api/teams/{team.Id}/views");

        Assert.Equal(chosen.Id, views![0].Id);
    }

    // ---- Editing ----

    [Fact]
    public async Task Updating_a_view_replaces_its_rules()
    {
        var team = await CreateTeamAsync("View Update", "SVR");
        await CreateIssueAsync(_admin, team.Id, new { title = "for ana", assignee = "ana" });
        await CreateIssueAsync(_admin, team.Id, new { title = "for ben", assignee = "ben" });

        var view = await CreateViewAsync(_admin, team.Id, new { name = "Whose", rules = new { assignee = "ana" } });
        Assert.Equal("for ana", Assert.Single((await ExecuteAsync(_admin, view.Id)).Items).Title);

        var response = await _admin.PutAsJsonAsync($"/api/views/{view.Id}", new
        {
            name = "Whose",
            scope = "Team",
            rules = new { assignee = "ben" },
        });
        response.EnsureSuccessStatusCode();

        Assert.Equal("for ben", Assert.Single((await ExecuteAsync(_admin, view.Id)).Items).Title);
    }

    [Fact]
    public async Task Sharing_a_personal_view_with_the_team_drops_its_owner()
    {
        var eng = await SeedTeamAsync("ENG");
        var view = await CreateViewAsync(_member, eng.Id, new { name = "Ben's, then everyone's", scope = "Personal" });

        var response = await _member.PutAsJsonAsync($"/api/views/{view.Id}", new
        {
            name = "Ben's, then everyone's",
            scope = "Team",
        });
        response.EnsureSuccessStatusCode();

        var shared = (await response.Content.ReadFromJsonAsync<ViewPayload>())!;
        Assert.Equal("Team", shared.Scope);
        Assert.Null(shared.Owner);
        // Now visible to the rest of the team.
        (await _admin.GetAsync($"/api/views/{view.Id}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Deleting_a_view_removes_it()
    {
        var team = await CreateTeamAsync("View Delete", "SVX");
        var view = await CreateViewAsync(_admin, team.Id, new { name = "Temporary" });

        var deleted = await _admin.DeleteAsync($"/api/views/{view.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/api/views/{view.Id}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_another_members_personal_view_is_404()
    {
        var eng = await SeedTeamAsync("ENG");
        var view = await CreateViewAsync(_member, eng.Id, new { name = "Ben's, hands off", scope = "Personal" });

        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync($"/api/views/{view.Id}")).StatusCode);
        (await _member.GetAsync($"/api/views/{view.Id}")).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("scope=nonsense")]
    [InlineData("name=")]
    public async Task Invalid_view_payloads_return_400(string field)
    {
        var eng = await SeedTeamAsync("ENG");
        var parts = field.Split('=', 2);
        var body = new Dictionary<string, object?> { ["name"] = "Valid", [parts[0]] = parts[1] };

        var response = await _admin.PostAsJsonAsync($"/api/teams/{eng.Id}/views", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
