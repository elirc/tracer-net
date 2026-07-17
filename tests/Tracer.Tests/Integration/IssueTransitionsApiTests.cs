using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

public class IssueTransitionsApiTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public IssueTransitionsApiTests(TracerApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private sealed record TeamPayload(Guid Id, string Key);
    private sealed record StatePayload(Guid Id, string Name, string Type, int Position);
    private sealed record IssuePayload(Guid Id, string Identifier, Guid StateId, string State, double Position);

    private async Task<(TeamPayload Team, List<StatePayload> States)> CreateTeamWithStatesAsync(string name, string key)
    {
        var response = await _client.PostAsJsonAsync("/api/teams", new { name, key });
        response.EnsureSuccessStatusCode();
        var team = (await response.Content.ReadFromJsonAsync<TeamPayload>())!;
        var states = (await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{team.Id}/states"))!;
        return (team, states);
    }

    private async Task<IssuePayload> CreateIssueAsync(Guid teamId, string title, Guid? stateId = null)
    {
        var response = await _client.PostAsJsonAsync($"/api/teams/{teamId}/issues", new { title, stateId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    [Fact]
    public async Task Valid_transition_moves_issue_and_appends_to_target_column()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Flow A", "FLA");
        var inProgress = states.Single(s => s.Name == "In Progress");
        var existing = await CreateIssueAsync(team.Id, "already in progress", inProgress.Id);
        var issue = await CreateIssueAsync(team.Id, "backlog item");

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions",
            new { stateId = inProgress.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var moved = await response.Content.ReadFromJsonAsync<IssuePayload>();
        Assert.Equal(inProgress.Id, moved!.StateId);
        Assert.Equal("In Progress", moved.State);
        Assert.True(moved.Position > existing.Position);
    }

    [Fact]
    public async Task Backlog_to_done_is_rejected_with_422()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Flow B", "FLB");
        var done = states.Single(s => s.Name == "Done");
        var issue = await CreateIssueAsync(team.Id, "cannot skip to done");

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions", new { stateId = done.Id });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid state transition", body);
    }

    [Fact]
    public async Task Done_to_canceled_is_rejected_with_422()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Flow C", "FLC");
        var inProgress = states.Single(s => s.Name == "In Progress");
        var done = states.Single(s => s.Name == "Done");
        var canceled = states.Single(s => s.Name == "Canceled");

        var issue = await CreateIssueAsync(team.Id, "walk the happy path", inProgress.Id);
        (await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions", new { stateId = done.Id }))
            .EnsureSuccessStatusCode();

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions", new { stateId = canceled.Id });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Transition_to_state_of_other_team_is_rejected_with_400()
    {
        var (teamA, _) = await CreateTeamWithStatesAsync("Flow D", "FLD");
        var (_, statesB) = await CreateTeamWithStatesAsync("Flow E", "FLE");
        var issue = await CreateIssueAsync(teamA.Id, "team-bound");

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions",
            new { stateId = statesB.Single(s => s.Name == "Todo").Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transition_to_current_state_is_a_no_op()
    {
        var (team, _) = await CreateTeamWithStatesAsync("Flow F", "FLF");
        var issue = await CreateIssueAsync(team.Id, "stay put");

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions", new { stateId = issue.StateId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var same = await response.Content.ReadFromJsonAsync<IssuePayload>();
        Assert.Equal(issue.StateId, same!.StateId);
        Assert.Equal(issue.Position, same.Position);
    }

    [Fact]
    public async Task Full_lifecycle_backlog_todo_inprogress_done()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Flow G", "FLG");
        var issue = await CreateIssueAsync(team.Id, "lifecycle");

        foreach (var name in new[] { "Todo", "In Progress", "Done" })
        {
            var target = states.Single(s => s.Name == name);
            var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions", new { stateId = target.Id });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var final = await _client.GetFromJsonAsync<IssuePayload>($"/api/issues/{issue.Id}");
        Assert.Equal("Done", final!.State);
    }
}
