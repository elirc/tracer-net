using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

public class ProjectsApiTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public ProjectsApiTests(TracerApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record ProjectPayload(Guid Id, Guid TeamId, string Name, string? Description, DateTimeOffset CreatedAt);

    private async Task<TeamPayload> GetTeamAsync(string key)
    {
        var teams = await _client.GetFromJsonAsync<List<TeamPayload>>("/api/teams");
        return teams!.Single(t => t.Key == key);
    }

    [Fact]
    public async Task List_returns_seeded_projects_for_team()
    {
        var eng = await GetTeamAsync("ENG");

        var projects = await _client.GetFromJsonAsync<List<ProjectPayload>>($"/api/teams/{eng.Id}/projects");

        Assert.NotNull(projects);
        Assert.Contains(projects, p => p.Name == "Public API");
        Assert.All(projects, p => Assert.Equal(eng.Id, p.TeamId));
    }

    [Fact]
    public async Task Create_get_update_delete_roundtrip()
    {
        var eng = await GetTeamAsync("ENG");

        var created = await _client.PostAsJsonAsync($"/api/teams/{eng.Id}/projects",
            new { name = "Billing", description = "Usage-based billing." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var project = await created.Content.ReadFromJsonAsync<ProjectPayload>();
        Assert.NotNull(project);

        var fetched = await _client.GetFromJsonAsync<ProjectPayload>($"/api/projects/{project.Id}");
        Assert.Equal("Billing", fetched!.Name);

        var updated = await _client.PutAsJsonAsync($"/api/projects/{project.Id}",
            new { name = "Billing v2", description = (string?)null });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedProject = await updated.Content.ReadFromJsonAsync<ProjectPayload>();
        Assert.Equal("Billing v2", updatedProject!.Name);
        Assert.Null(updatedProject.Description);

        var deleted = await _client.DeleteAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var gone = await _client.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Create_for_unknown_team_returns_404()
    {
        var response = await _client.PostAsJsonAsync($"/api/teams/{Guid.NewGuid()}/projects", new { name = "Ghost" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_without_name_returns_400()
    {
        var eng = await GetTeamAsync("ENG");

        var response = await _client.PostAsJsonAsync($"/api/teams/{eng.Id}/projects", new { description = "no name" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
