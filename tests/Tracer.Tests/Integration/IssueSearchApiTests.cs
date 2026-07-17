using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

public class IssueSearchApiTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public IssueSearchApiTests(TracerApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private sealed record TeamPayload(Guid Id, string Key);
    private sealed record StatePayload(Guid Id, string Name, string Type, int Position);
    private sealed record ProjectPayload(Guid Id, string Name);
    private sealed record LabelPayload(Guid Id, string Name, string Color);
    private sealed record CyclePayload(Guid Id, int Number, string Status);
    private sealed record IssuePayload(
        Guid Id, Guid TeamId, string Identifier, string Title, string? Description, string Priority,
        string? Assignee, Guid StateId, string State, Guid? ProjectId, Guid? CycleId, double Position,
        List<LabelPayload> Labels, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record PagePayload(List<IssuePayload> Items, int Page, int PageSize, int Total, int TotalPages);

    /// <summary>
    /// Builds a properly escaped query string. Tests that pass raw text (a bare
    /// "%", a phrase with spaces) depend on it reaching the server intact.
    /// </summary>
    private static string QueryString(params (string Key, string Value)[] filters) =>
        "?" + string.Join("&", filters.Select(f => $"{f.Key}={Uri.EscapeDataString(f.Value)}"));

    private async Task<PagePayload> SearchAsync(params (string Key, string Value)[] filters)
    {
        var response = await _client.GetAsync($"/api/issues{QueryString(filters)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PagePayload>())!;
    }

    private async Task<TeamPayload> GetTeamAsync(string key)
    {
        var teams = await _client.GetFromJsonAsync<List<TeamPayload>>("/api/teams");
        return teams!.Single(t => t.Key == key);
    }

    private async Task<TeamPayload> CreateTeamAsync(string name, string key)
    {
        var response = await _client.PostAsJsonAsync("/api/teams", new { name, key });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TeamPayload>())!;
    }

    private async Task CreateIssueAsync(Guid teamId, string title, string priority = "None")
    {
        var response = await _client.PostAsJsonAsync($"/api/teams/{teamId}/issues", new { title, priority });
        response.EnsureSuccessStatusCode();
    }

    // Tests in this class share a fixture and add issues of their own, so every
    // assertion about counts is scoped to a team rather than the whole database.

    [Fact]
    public async Task Search_without_filters_spans_teams_and_defaults_to_the_first_page()
    {
        var page = await SearchAsync();

        Assert.True(page.Total >= 5); // at least the seeds
        Assert.Equal(1, page.Page);
        Assert.Equal(25, page.PageSize);
        Assert.Contains(page.Items, i => i.Identifier == "ENG-1");
        Assert.Contains(page.Items, i => i.Identifier == "DES-1");
    }

    [Fact]
    public async Task Search_filters_by_team()
    {
        var eng = await GetTeamAsync("ENG");

        var page = await SearchAsync(("teamId", eng.Id.ToString()));

        Assert.Equal(4, page.Total);
        Assert.All(page.Items, i => Assert.Equal(eng.Id, i.TeamId));
    }

    [Fact]
    public async Task Search_filters_by_assignee_case_insensitively()
    {
        var eng = await GetTeamAsync("ENG");

        var page = await SearchAsync(("teamId", eng.Id.ToString()), ("assignee", "ANA"));

        Assert.Equal(2, page.Total);
        Assert.All(page.Items, i => Assert.Equal("ana", i.Assignee));
    }

    [Fact]
    public async Task Search_filters_by_priority()
    {
        var eng = await GetTeamAsync("ENG");

        var page = await SearchAsync(("teamId", eng.Id.ToString()), ("priority", "Urgent"));

        Assert.Equal("Rate limiting returns 500 instead of 429", Assert.Single(page.Items).Title);
    }

    [Fact]
    public async Task Search_filters_by_state()
    {
        var eng = await GetTeamAsync("ENG");
        var states = (await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{eng.Id}/states"))!;
        var done = states.Single(s => s.Name == "Done");

        var page = await SearchAsync(("stateId", done.Id.ToString()));

        Assert.Equal("Upgrade CI runners to .NET 10", Assert.Single(page.Items).Title);
    }

    [Fact]
    public async Task Search_filters_by_project()
    {
        var eng = await GetTeamAsync("ENG");
        var projects = (await _client.GetFromJsonAsync<List<ProjectPayload>>($"/api/teams/{eng.Id}/projects"))!;
        var api = projects.Single(p => p.Name == "Public API");

        var page = await SearchAsync(("projectId", api.Id.ToString()));

        Assert.Equal(2, page.Total);
        Assert.All(page.Items, i => Assert.Equal(api.Id, i.ProjectId));
    }

    [Fact]
    public async Task Search_filters_by_label()
    {
        var eng = await GetTeamAsync("ENG");
        var labels = (await _client.GetFromJsonAsync<List<LabelPayload>>($"/api/teams/{eng.Id}/labels"))!;
        var bug = labels.Single(l => l.Name == "bug");

        var page = await SearchAsync(("labelId", bug.Id.ToString()));

        var only = Assert.Single(page.Items);
        Assert.Contains(only.Labels, l => l.Name == "bug");
    }

    [Fact]
    public async Task Search_filters_by_cycle()
    {
        var eng = await GetTeamAsync("ENG");
        var cycles = (await _client.GetFromJsonAsync<List<CyclePayload>>($"/api/teams/{eng.Id}/cycles"))!;
        var active = cycles.Single(c => c.Status == "Active");

        var page = await SearchAsync(("cycleId", active.Id.ToString()));

        Assert.Equal(3, page.Total);
        Assert.All(page.Items, i => Assert.Equal(active.Id, i.CycleId));
    }

    [Fact]
    public async Task Free_text_matches_title_and_description()
    {
        var byTitle = await SearchAsync(("q", "rate limiting"));
        Assert.Equal("Rate limiting returns 500 instead of 429", Assert.Single(byTitle.Items).Title);

        // "scoped personal access tokens" only ever appears in a description.
        var byDescription = await SearchAsync(("q", "scoped personal"));
        Assert.Equal("Design authentication flow for API tokens", Assert.Single(byDescription.Items).Title);
    }

    [Fact]
    public async Task Free_text_is_case_insensitive_and_matches_substrings()
    {
        var page = await SearchAsync(("q", "AUTHENTICATION"));

        Assert.Equal("Design authentication flow for API tokens", Assert.Single(page.Items).Title);
    }

    [Fact]
    public async Task Free_text_treats_wildcards_as_literal_characters()
    {
        // Leaked into the LIKE pattern, either of these would match everything.
        Assert.Equal(0, (await SearchAsync(("q", "%"))).Total);
        Assert.Equal(0, (await SearchAsync(("q", "_"))).Total);
    }

    [Fact]
    public async Task Filters_combine_with_and()
    {
        var eng = await GetTeamAsync("ENG");

        var page = await SearchAsync(
            ("teamId", eng.Id.ToString()),
            ("assignee", "ana"),
            ("priority", "High"));

        Assert.Equal("Design authentication flow for API tokens", Assert.Single(page.Items).Title);
    }

    [Fact]
    public async Task Filters_that_match_nothing_return_an_empty_page()
    {
        var page = await SearchAsync(("assignee", "nobody"));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.Equal(0, page.TotalPages);
    }

    [Fact]
    public async Task Sort_by_priority_puts_the_most_urgent_first_and_unprioritised_last()
    {
        var team = await CreateTeamAsync("Sort Priority", "SPR");
        foreach (var priority in new[] { "Low", "None", "Urgent", "Medium", "High" })
        {
            await CreateIssueAsync(team.Id, $"{priority} issue", priority);
        }

        var page = await SearchAsync(("teamId", team.Id.ToString()), ("sort", "Priority"), ("order", "Asc"));

        Assert.Equal(
            ["Urgent issue", "High issue", "Medium issue", "Low issue", "None issue"],
            page.Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task Sort_by_title_respects_direction()
    {
        var team = await CreateTeamAsync("Sort Title", "STI");
        foreach (var title in new[] { "cherry", "apple", "banana" })
        {
            await CreateIssueAsync(team.Id, title);
        }

        var ascending = await SearchAsync(("teamId", team.Id.ToString()), ("sort", "Title"), ("order", "Asc"));
        var descending = await SearchAsync(("teamId", team.Id.ToString()), ("sort", "Title"), ("order", "Desc"));

        Assert.Equal(["apple", "banana", "cherry"], ascending.Items.Select(i => i.Title).ToArray());
        Assert.Equal(["cherry", "banana", "apple"], descending.Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task Sort_by_number_follows_creation_order()
    {
        var team = await CreateTeamAsync("Sort Number", "SNU");
        await CreateIssueAsync(team.Id, "first");
        await CreateIssueAsync(team.Id, "second");
        await CreateIssueAsync(team.Id, "third");

        var page = await SearchAsync(("teamId", team.Id.ToString()), ("sort", "Number"), ("order", "Asc"));

        Assert.Equal(["first", "second", "third"], page.Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task Paging_walks_the_whole_result_set_without_repeats_or_gaps()
    {
        var team = await CreateTeamAsync("Paging", "PAG");
        // Identical titles: every issue shares the sort key, so only a
        // deterministic tiebreak keeps pages from overlapping or skipping rows.
        for (var i = 0; i < 7; i++)
        {
            await CreateIssueAsync(team.Id, "same title");
        }

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            var result = await SearchAsync(
                ("teamId", team.Id.ToString()),
                ("sort", "Title"),
                ("order", "Asc"),
                ("page", page.ToString()),
                ("pageSize", "2"));

            Assert.Equal(7, result.Total);
            Assert.Equal(4, result.TotalPages);
            seen.AddRange(result.Items.Select(i => i.Id));
        }

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public async Task Page_beyond_the_end_is_empty_but_still_reports_the_total()
    {
        var eng = await GetTeamAsync("ENG");

        var page = await SearchAsync(("teamId", eng.Id.ToString()), ("page", "99"));

        Assert.Empty(page.Items);
        Assert.Equal(4, page.Total);
        Assert.Equal(99, page.Page);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("sort=nonsense")]
    [InlineData("order=nonsense")]
    [InlineData("priority=nonsense")]
    [InlineData("teamId=not-a-guid")]
    public async Task Invalid_query_parameters_return_400(string queryString)
    {
        var response = await _client.GetAsync($"/api/issues?{queryString}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
