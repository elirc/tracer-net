using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

public class TeamsApiTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public TeamsApiTests(TracerApiFactory factory)
    {
        _client = factory.CreateAdminClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key, DateTimeOffset CreatedAt);
    private sealed record StatePayload(Guid Id, string Name, string Type, int Position, string Color);

    [Fact]
    public async Task List_returns_seeded_teams()
    {
        var teams = await _client.GetListAsync<TeamPayload>("/api/teams");

        Assert.NotNull(teams);
        Assert.Contains(teams, t => t.Key == "ENG");
        Assert.Contains(teams, t => t.Key == "DES");
    }

    [Fact]
    public async Task Create_get_update_delete_roundtrip()
    {
        var created = await _client.PostAsJsonAsync("/api/teams", new { name = "Platform", key = "PLT" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var team = await created.Content.ReadFromJsonAsync<TeamPayload>();
        Assert.NotNull(team);
        Assert.Equal("PLT", team.Key);

        var fetched = await _client.GetFromJsonAsync<TeamPayload>($"/api/teams/{team.Id}");
        Assert.NotNull(fetched);
        Assert.Equal("Platform", fetched.Name);

        var updated = await _client.PutAsJsonAsync($"/api/teams/{team.Id}", new { name = "Platform Core", key = "PLT" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedTeam = await updated.Content.ReadFromJsonAsync<TeamPayload>();
        Assert.Equal("Platform Core", updatedTeam!.Name);

        var deleted = await _client.DeleteAsync($"/api/teams/{team.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var gone = await _client.GetAsync($"/api/teams/{team.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Create_seeds_default_workflow_states()
    {
        var created = await _client.PostAsJsonAsync("/api/teams", new { name = "Ops", key = "OPS" });
        var team = await created.Content.ReadFromJsonAsync<TeamPayload>();

        var states = await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{team!.Id}/states");

        Assert.NotNull(states);
        Assert.Equal(5, states.Count);
        Assert.Equal(["Backlog", "Todo", "In Progress", "Done", "Canceled"],
            states.OrderBy(s => s.Position).Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task Create_with_duplicate_key_returns_409()
    {
        var response = await _client.PostAsJsonAsync("/api/teams", new { name = "Engineering Two", key = "ENG" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_invalid_key_returns_400()
    {
        var response = await _client.PostAsJsonAsync("/api/teams", new { name = "Bad", key = "not-a-key" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_unknown_team_returns_404()
    {
        var response = await _client.GetAsync($"/api/teams/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
