using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

public class WorkflowStatesApiTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public WorkflowStatesApiTests(TracerApiFactory factory)
    {
        _client = factory.CreateAdminClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record StatePayload(Guid Id, Guid TeamId, string Name, string Type, int Position, string Color);

    private async Task<TeamPayload> CreateTeamAsync(string name, string key)
    {
        var response = await _client.PostAsJsonAsync("/api/teams", new { name, key });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TeamPayload>())!;
    }

    private Task<List<StatePayload>?> GetStatesAsync(Guid teamId) =>
        _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{teamId}/states");

    [Fact]
    public async Task Create_appends_custom_state_at_end_by_default()
    {
        var team = await CreateTeamAsync("States A", "STA");

        var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/states",
            new { name = "In Review", type = "InProgress", color = "#26b5ce" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var state = await response.Content.ReadFromJsonAsync<StatePayload>();
        Assert.Equal(5, state!.Position);
        Assert.Equal("InProgress", state.Type);
    }

    [Fact]
    public async Task Create_at_position_shifts_siblings()
    {
        var team = await CreateTeamAsync("States B", "STB");

        var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/states",
            new { name = "Triage", type = "Backlog", position = 1 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var states = await GetStatesAsync(team.Id);
        Assert.Equal(["Backlog", "Triage", "Todo", "In Progress", "Done", "Canceled"],
            states!.OrderBy(s => s.Position).Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task Update_renames_recolors_and_reorders()
    {
        var team = await CreateTeamAsync("States C", "STC");
        var states = await GetStatesAsync(team.Id);
        var todo = states!.Single(s => s.Name == "Todo");

        var response = await _client.PutAsJsonAsync($"/api/states/{todo.Id}",
            new { name = "Up Next", color = "#aabbcc", position = 0 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await GetStatesAsync(team.Id);
        Assert.Equal(["Up Next", "Backlog", "In Progress", "Done", "Canceled"],
            after!.OrderBy(s => s.Position).Select(s => s.Name).ToArray());
        Assert.Equal([0, 1, 2, 3, 4], after.OrderBy(s => s.Position).Select(s => s.Position).ToArray());
    }

    [Fact]
    public async Task Create_duplicate_name_returns_409()
    {
        var team = await CreateTeamAsync("States D", "STD");

        var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/states",
            new { name = "Todo", type = "Todo" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Delete_empty_state_succeeds_but_state_with_issues_returns_409()
    {
        var team = await CreateTeamAsync("States E", "STE");
        var states = await GetStatesAsync(team.Id);
        var backlog = states!.Single(s => s.Name == "Backlog");
        var todo = states.Single(s => s.Name == "Todo");

        // Occupy Backlog (default state) with an issue.
        await _client.PostAsJsonAsync($"/api/teams/{team.Id}/issues", new { title = "occupier" });

        var blocked = await _client.DeleteAsync($"/api/states/{backlog.Id}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        var ok = await _client.DeleteAsync($"/api/states/{todo.Id}");
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        var after = await GetStatesAsync(team.Id);
        Assert.DoesNotContain(after!, s => s.Name == "Todo");
    }
}
