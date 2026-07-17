using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

public class IssuesApiTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public IssuesApiTests(TracerApiFactory factory)
    {
        _client = factory.CreateAdminClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record StatePayload(Guid Id, string Name, string Type, int Position);
    private sealed record LabelPayload(Guid Id, string Name, string Color);
    private sealed record IssuePayload(
        Guid Id, Guid TeamId, string Identifier, int Number, string Title, string? Description,
        string Priority, int? Estimate, Guid StateId, string State, Guid? ProjectId, Guid? CycleId,
        double Position, List<LabelPayload> Labels, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private async Task<TeamPayload> GetTeamAsync(string key)
    {
        var teams = await _client.GetListAsync<TeamPayload>("/api/teams");
        return teams!.Single(t => t.Key == key);
    }

    [Fact]
    public async Task List_returns_seeded_issues_with_identifiers()
    {
        var eng = await GetTeamAsync("ENG");

        var issues = await _client.GetListAsync<IssuePayload>($"/api/teams/{eng.Id}/issues");

        Assert.NotNull(issues);
        // Other tests in this class may add issues (shared fixture), so assert on the seeds only.
        Assert.True(issues.Count >= 4);
        Assert.Contains(issues, i => i.Identifier == "ENG-1");
        Assert.Contains(issues, i => i.Labels.Any(l => l.Name == "bug"));
    }

    [Fact]
    public async Task Create_assigns_number_default_state_and_position()
    {
        var created = await _client.PostAsJsonAsync("/api/teams", new { name = "Mobile", key = "MOB" });
        var team = await created.Content.ReadFromJsonAsync<TeamPayload>();

        var first = await _client.PostAsJsonAsync($"/api/teams/{team!.Id}/issues",
            new { title = "First issue", priority = "High", estimate = 3 });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var issue1 = await first.Content.ReadFromJsonAsync<IssuePayload>();
        Assert.NotNull(issue1);
        Assert.Equal("MOB-1", issue1.Identifier);
        Assert.Equal("Backlog", issue1.State);
        Assert.Equal("High", issue1.Priority);
        Assert.Equal(1, issue1.Position);

        var second = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/issues", new { title = "Second issue" });
        var issue2 = await second.Content.ReadFromJsonAsync<IssuePayload>();
        Assert.Equal("MOB-2", issue2!.Identifier);
        Assert.Equal(2, issue2.Position);
    }

    [Fact]
    public async Task Create_with_explicit_state_uses_that_state()
    {
        var eng = await GetTeamAsync("ENG");
        var states = await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{eng.Id}/states");
        var inProgress = states!.Single(s => s.Name == "In Progress");

        var created = await _client.PostAsJsonAsync($"/api/teams/{eng.Id}/issues",
            new { title = "Straight to work", stateId = inProgress.Id });
        var issue = await created.Content.ReadFromJsonAsync<IssuePayload>();

        Assert.Equal(inProgress.Id, issue!.StateId);
        Assert.Equal("In Progress", issue.State);
    }

    [Fact]
    public async Task Create_with_foreign_state_returns_400()
    {
        var eng = await GetTeamAsync("ENG");
        var des = await GetTeamAsync("DES");
        var desStates = await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{des.Id}/states");

        var response = await _client.PostAsJsonAsync($"/api/teams/{eng.Id}/issues",
            new { title = "Cross-team state", stateId = desStates!.First().Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_changes_fields_and_bumps_updated_at()
    {
        var eng = await GetTeamAsync("ENG");
        var created = await _client.PostAsJsonAsync($"/api/teams/{eng.Id}/issues",
            new { title = "Editable", priority = "Low" });
        var issue = await created.Content.ReadFromJsonAsync<IssuePayload>();

        var updated = await _client.PutAsJsonAsync($"/api/issues/{issue!.Id}",
            new { title = "Edited", description = "now with details", priority = "Urgent", estimate = 8, projectId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var edited = await updated.Content.ReadFromJsonAsync<IssuePayload>();

        Assert.Equal("Edited", edited!.Title);
        Assert.Equal("Urgent", edited.Priority);
        Assert.Equal(8, edited.Estimate);
        Assert.True(edited.UpdatedAt >= issue.UpdatedAt);
    }

    [Fact]
    public async Task Delete_removes_issue()
    {
        var eng = await GetTeamAsync("ENG");
        var created = await _client.PostAsJsonAsync($"/api/teams/{eng.Id}/issues", new { title = "Doomed" });
        var issue = await created.Content.ReadFromJsonAsync<IssuePayload>();

        var deleted = await _client.DeleteAsync($"/api/issues/{issue!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var gone = await _client.GetAsync($"/api/issues/{issue.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Create_without_title_returns_400()
    {
        var eng = await GetTeamAsync("ENG");

        var response = await _client.PostAsJsonAsync($"/api/teams/{eng.Id}/issues", new { description = "untitled" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
