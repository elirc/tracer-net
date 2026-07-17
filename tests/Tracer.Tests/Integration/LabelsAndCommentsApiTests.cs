using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

public class LabelsAndCommentsApiTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public LabelsAndCommentsApiTests(TracerApiFactory factory)
    {
        _client = factory.CreateAdminClient();
    }

    private sealed record TeamPayload(Guid Id, string Key);
    private sealed record LabelPayload(Guid Id, Guid TeamId, string Name, string Color);
    private sealed record IssueLabelPayload(Guid Id, string Name, string Color);
    private sealed record IssuePayload(Guid Id, string Identifier, List<IssueLabelPayload> Labels);
    private sealed record CommentPayload(Guid Id, Guid IssueId, string Author, string Body, DateTimeOffset CreatedAt);

    private async Task<TeamPayload> CreateTeamAsync(string name, string key)
    {
        var response = await _client.PostAsJsonAsync("/api/teams", new { name, key });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TeamPayload>())!;
    }

    private async Task<IssuePayload> CreateIssueAsync(Guid teamId, string title)
    {
        var response = await _client.PostAsJsonAsync($"/api/teams/{teamId}/issues", new { title });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    [Fact]
    public async Task Label_crud_roundtrip()
    {
        var team = await CreateTeamAsync("Lab A", "LBA");

        var created = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/labels",
            new { name = "regression", color = "#eb5757" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var label = await created.Content.ReadFromJsonAsync<LabelPayload>();

        var updated = await _client.PutAsJsonAsync($"/api/labels/{label!.Id}",
            new { name = "regression-p0", color = "#ff0000" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var listed = await _client.GetFromJsonAsync<List<LabelPayload>>($"/api/teams/{team.Id}/labels");
        Assert.Contains(listed!, l => l.Name == "regression-p0");

        var deleted = await _client.DeleteAsync($"/api/labels/{label.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task Duplicate_label_name_returns_409()
    {
        var team = await CreateTeamAsync("Lab B", "LBB");
        await _client.PostAsJsonAsync($"/api/teams/{team.Id}/labels", new { name = "dup" });

        var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/labels", new { name = "dup" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Attach_and_detach_label_on_issue()
    {
        var team = await CreateTeamAsync("Lab C", "LBC");
        var issue = await CreateIssueAsync(team.Id, "labeled work");
        var created = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/labels", new { name = "infra" });
        var label = await created.Content.ReadFromJsonAsync<LabelPayload>();

        var attach = await _client.PutAsync($"/api/issues/{issue.Id}/labels/{label!.Id}", null);
        Assert.Equal(HttpStatusCode.NoContent, attach.StatusCode);

        // Attaching twice is idempotent.
        var again = await _client.PutAsync($"/api/issues/{issue.Id}/labels/{label.Id}", null);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        var withLabel = await _client.GetFromJsonAsync<IssuePayload>($"/api/issues/{issue.Id}");
        Assert.Single(withLabel!.Labels);
        Assert.Equal("infra", withLabel.Labels[0].Name);

        var detach = await _client.DeleteAsync($"/api/issues/{issue.Id}/labels/{label.Id}");
        Assert.Equal(HttpStatusCode.NoContent, detach.StatusCode);

        var without = await _client.GetFromJsonAsync<IssuePayload>($"/api/issues/{issue.Id}");
        Assert.Empty(without!.Labels);
    }

    [Fact]
    public async Task Attaching_label_from_other_team_returns_400()
    {
        var teamA = await CreateTeamAsync("Lab D", "LBD");
        var teamB = await CreateTeamAsync("Lab E", "LBE");
        var issue = await CreateIssueAsync(teamA.Id, "team-scoped labels");
        var created = await _client.PostAsJsonAsync($"/api/teams/{teamB.Id}/labels", new { name = "foreign" });
        var label = await created.Content.ReadFromJsonAsync<LabelPayload>();

        var response = await _client.PutAsync($"/api/issues/{issue.Id}/labels/{label!.Id}", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Comment_crud_roundtrip_ordered_by_creation()
    {
        var team = await CreateTeamAsync("Com A", "CMA");
        var issue = await CreateIssueAsync(team.Id, "discussed");

        var first = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { body = "first!" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstComment = await first.Content.ReadFromJsonAsync<CommentPayload>();
        // Authorship comes from the credential, not from the request body.
        Assert.Equal("ana", firstComment!.Author);

        await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { body = "second" });

        var comments = await _client.GetFromJsonAsync<List<CommentPayload>>($"/api/issues/{issue.Id}/comments");
        Assert.Equal(2, comments!.Count);
        Assert.Equal(["first!", "second"], comments.Select(c => c.Body).ToArray());

        var updated = await _client.PutAsJsonAsync($"/api/comments/{firstComment.Id}", new { body = "edited" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var deleted = await _client.DeleteAsync($"/api/comments/{firstComment.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var remaining = await _client.GetFromJsonAsync<List<CommentPayload>>($"/api/issues/{issue.Id}/comments");
        Assert.Single(remaining!);
    }

    [Fact]
    public async Task Comment_on_unknown_issue_returns_404()
    {
        var response = await _client.PostAsJsonAsync($"/api/issues/{Guid.NewGuid()}/comments",
            new { body = "boo" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Comment_without_body_returns_400()
    {
        var team = await CreateTeamAsync("Com B", "CMB");
        var issue = await CreateIssueAsync(team.Id, "strict");

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
