using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// The denial matrix: for every shape of endpoint, what each kind of caller gets.
///
/// The seeded workspace gives us all three: <c>ana</c> is a workspace admin,
/// <c>ben</c> is a member of Engineering only, and <c>dana</c> is a member of
/// Design only — so for anything Engineering owns, dana is a real, authenticated,
/// entirely foreign caller. That is the interesting case, and the one a test
/// suite that only ever authenticates as an admin never exercises.
/// </summary>
public class AuthorizationApiTests : IClassFixture<TracerApiFactory>
{
    private readonly TracerApiFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _member;
    private readonly HttpClient _foreigner;

    public AuthorizationApiTests(TracerApiFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateAdminClient();
        _member = factory.CreateEngMemberClient();
        _foreigner = factory.CreateDesMemberClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record IssuePayload(Guid Id, Guid TeamId, string Title, Guid StateId);
    private sealed record StatePayload(Guid Id, string Name, string Type, int Position);
    private sealed record LabelPayload(Guid Id, string Name);
    private sealed record ProjectPayload(Guid Id, string Name);
    private sealed record CyclePayload(Guid Id, int Number);
    private sealed record CommentPayload(Guid Id, string Author, string Body);
    private sealed record PagedIssues(List<IssuePayload> Items, int Total);

    private async Task<TeamPayload> TeamAsync(string key)
    {
        var teams = await _admin.GetListAsync<TeamPayload>("/api/teams");
        return teams!.Single(t => t.Key == key);
    }

    private async Task<IssuePayload> EngIssueAsync(string title)
    {
        var eng = await TeamAsync("ENG");
        var created = await _admin.PostAsJsonAsync($"/api/teams/{eng.Id}/issues", new { title });
        return (await created.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    // ---- Reading a team ----

    [Fact]
    public async Task A_member_sees_only_their_own_teams_in_the_list()
    {
        var teams = await _member.GetListAsync<TeamPayload>("/api/teams");

        Assert.Equal(["ENG"], teams!.Select(t => t.Key).ToArray());
    }

    [Fact]
    public async Task An_admin_sees_every_team_in_the_list()
    {
        var teams = await _admin.GetListAsync<TeamPayload>("/api/teams");

        Assert.Contains(teams!, t => t.Key == "ENG");
        Assert.Contains(teams!, t => t.Key == "DES");
    }

    [Fact]
    public async Task A_foreign_team_is_404_not_403()
    {
        var eng = await TeamAsync("ENG");

        var response = await _foreigner.GetAsync($"/api/teams/{eng.Id}");

        // Not 403: that would confirm the id names a real team, letting anyone
        // map the workspace by watching which ids answer differently.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A team that exists but is not yours must be reported exactly like one that
    /// was never real — otherwise the difference between the two answers is
    /// itself the disclosure.
    ///
    /// "Exactly" means status and title. The bodies are not byte-identical and
    /// should not be: <c>detail</c> echoes the id the caller just asked about,
    /// which they already knew, and <c>traceId</c> is per-request by design.
    /// Asserting on the whole body would only pin those two.
    /// </summary>
    [Fact]
    public async Task A_foreign_team_and_a_nonexistent_team_are_reported_identically()
    {
        var eng = await TeamAsync("ENG");

        var foreign = await _foreigner.GetAsync($"/api/teams/{eng.Id}");
        var absent = await _foreigner.GetAsync($"/api/teams/{Guid.NewGuid()}");

        Assert.Equal(absent.StatusCode, foreign.StatusCode);
        Assert.Equal(
            await TitleOfAsync(absent),
            await TitleOfAsync(foreign));
    }

    private static async Task<string?> TitleOfAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("title").GetString();

    // ---- Role gates ----

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Team_administration_is_admin_only(string method)
    {
        var eng = await TeamAsync("ENG");
        var response = method switch
        {
            "POST" => await _member.PostAsJsonAsync("/api/teams", new { name = "Sneaky", key = "SNK" }),
            "PUT" => await _member.PutAsJsonAsync($"/api/teams/{eng.Id}", new { name = "Renamed", key = "ENG" }),
            _ => await _member.DeleteAsync($"/api/teams/{eng.Id}"),
        };

        // 403, not 404: a member of ENG can plainly see ENG. What they lack is
        // the role, and saying so is not a disclosure.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task User_administration_is_admin_only()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _member.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _member.PostAsJsonAsync("/api/users", new { handle = "mole", name = "Mole" })).StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_add_themselves_to_another_team()
    {
        var des = await TeamAsync("DES");
        var users = await _admin.GetListAsync<UserRow>("/api/users");
        var ben = users!.Single(u => u.Handle == "ben");

        var response = await _member.PutAsync($"/api/users/{ben.Id}/teams/{des.Id}", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed record UserRow(Guid Id, string Handle, string Role);

    /// <summary>
    /// A member configures their own team's workflow, labels, projects and
    /// cycles. Requiring an admin for that would make admins a ticket queue for
    /// routine work, which is how "just give everyone admin" happens.
    /// </summary>
    [Fact]
    public async Task A_member_may_configure_their_own_teams_workflow_and_labels()
    {
        var eng = await TeamAsync("ENG");

        var label = await _member.PostAsJsonAsync($"/api/teams/{eng.Id}/labels", new { name = "member-made" });
        Assert.Equal(HttpStatusCode.Created, label.StatusCode);

        var state = await _member.PostAsJsonAsync($"/api/teams/{eng.Id}/states",
            new { name = "Member Review", type = "InProgress" });
        Assert.Equal(HttpStatusCode.Created, state.StatusCode);
    }

    // ---- Every team-scoped resource, seen from a foreign team ----

    [Fact]
    public async Task Foreign_team_subresources_are_all_404()
    {
        var eng = await TeamAsync("ENG");

        foreach (var route in new[] { "issues", "projects", "labels", "states", "cycles", "members" })
        {
            var response = await _foreigner.GetAsync($"/api/teams/{eng.Id}/{route}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Writing_into_a_foreign_team_is_404()
    {
        var eng = await TeamAsync("ENG");

        var issue = await _foreigner.PostAsJsonAsync($"/api/teams/{eng.Id}/issues", new { title = "trespass" });
        Assert.Equal(HttpStatusCode.NotFound, issue.StatusCode);

        var project = await _foreigner.PostAsJsonAsync($"/api/teams/{eng.Id}/projects", new { name = "trespass" });
        Assert.Equal(HttpStatusCode.NotFound, project.StatusCode);

        var label = await _foreigner.PostAsJsonAsync($"/api/teams/{eng.Id}/labels", new { name = "trespass" });
        Assert.Equal(HttpStatusCode.NotFound, label.StatusCode);
    }

    [Fact]
    public async Task A_foreign_issue_cannot_be_read_updated_transitioned_reordered_or_deleted()
    {
        var issue = await EngIssueAsync("private to engineering");

        Assert.Equal(HttpStatusCode.NotFound, (await _foreigner.GetAsync($"/api/issues/{issue.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _foreigner.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "hijacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _foreigner.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions",
                new { stateId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _foreigner.PostAsJsonAsync($"/api/issues/{issue.Id}/reorder", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _foreigner.DeleteAsync($"/api/issues/{issue.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_foreign_issues_comments_are_out_of_reach()
    {
        var issue = await EngIssueAsync("has a discussion");
        await _admin.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { body = "internal" });

        Assert.Equal(HttpStatusCode.NotFound,
            (await _foreigner.GetAsync($"/api/issues/{issue.Id}/comments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _foreigner.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { body = "hello?" })).StatusCode);
    }

    [Fact]
    public async Task A_foreign_project_cycle_label_and_state_are_all_404_by_id()
    {
        var eng = await TeamAsync("ENG");

        var project = await (await _admin.PostAsJsonAsync($"/api/teams/{eng.Id}/projects", new { name = "secret" }))
            .Content.ReadFromJsonAsync<ProjectPayload>();
        var label = await (await _admin.PostAsJsonAsync($"/api/teams/{eng.Id}/labels", new { name = "secret-label" }))
            .Content.ReadFromJsonAsync<LabelPayload>();
        var now = DateTimeOffset.UtcNow;
        var cycle = await (await _admin.PostAsJsonAsync($"/api/teams/{eng.Id}/cycles",
                new { name = "secret cycle", startsAt = now.AddDays(400), endsAt = now.AddDays(414) }))
            .Content.ReadFromJsonAsync<CyclePayload>();
        var states = await _admin.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{eng.Id}/states");

        Assert.Equal(HttpStatusCode.NotFound, (await _foreigner.GetAsync($"/api/projects/{project!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _foreigner.GetAsync($"/api/labels/{label!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _foreigner.GetAsync($"/api/cycles/{cycle!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _foreigner.GetAsync($"/api/cycles/{cycle.Id}/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _foreigner.GetAsync($"/api/states/{states![0].Id}")).StatusCode);
    }

    // ---- Search ----

    /// <summary>
    /// Search is the one read that crosses teams without the caller naming an id,
    /// so it is the one place team scoping has to live in the query rather than in
    /// a check on a route parameter.
    /// </summary>
    [Fact]
    public async Task Search_never_returns_another_teams_issues()
    {
        await EngIssueAsync("unmistakable-engineering-marker");

        var results = await _foreigner.GetFromJsonAsync<PagedIssues>("/api/issues?q=unmistakable-engineering-marker");

        Assert.Empty(results!.Items);
        Assert.Equal(0, results.Total);
    }

    [Fact]
    public async Task Search_filtered_to_a_foreign_team_returns_nothing_rather_than_that_team()
    {
        var eng = await TeamAsync("ENG");
        await EngIssueAsync("another engineering issue");

        var results = await _foreigner.GetFromJsonAsync<PagedIssues>($"/api/issues?teamId={eng.Id}");

        Assert.Empty(results!.Items);
    }

    [Fact]
    public async Task Search_as_an_admin_still_crosses_every_team()
    {
        await EngIssueAsync("visible-to-the-admin");

        var results = await _admin.GetFromJsonAsync<PagedIssues>("/api/issues?q=visible-to-the-admin");

        Assert.Single(results!.Items);
    }

    // ---- Comment authorship ----

    [Fact]
    public async Task A_member_cannot_edit_or_delete_a_teammates_comment()
    {
        var issue = await EngIssueAsync("whose words are these");
        var comment = await (await _admin.PostAsJsonAsync($"/api/issues/{issue.Id}/comments",
            new { body = "ana wrote this" })).Content.ReadFromJsonAsync<CommentPayload>();

        // ben is on the same team and can read it...
        Assert.Equal(HttpStatusCode.OK, (await _member.GetAsync($"/api/comments/{comment!.Id}")).StatusCode);

        // ...but membership is not authorship. A 403 here, not a 404: he can see it.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _member.PutAsJsonAsync($"/api/comments/{comment.Id}", new { body = "ben wrote this" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _member.DeleteAsync($"/api/comments/{comment.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_author_edits_their_own_comment_and_an_admin_edits_anyones()
    {
        var issue = await EngIssueAsync("moderation");
        var comment = await (await _member.PostAsJsonAsync($"/api/issues/{issue.Id}/comments",
            new { body = "ben's own" })).Content.ReadFromJsonAsync<CommentPayload>();
        Assert.Equal("ben", comment!.Author);

        Assert.Equal(HttpStatusCode.OK,
            (await _member.PutAsJsonAsync($"/api/comments/{comment.Id}", new { body = "ben's edit" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PutAsJsonAsync($"/api/comments/{comment.Id}", new { body = "moderated" })).StatusCode);
    }

    [Fact]
    public async Task A_comment_cannot_be_posted_under_someone_elses_name()
    {
        var issue = await EngIssueAsync("impersonation attempt");

        // The field is not in the contract; sending it anyway changes nothing.
        var created = await _member.PostAsJsonAsync($"/api/issues/{issue.Id}/comments",
            new { author = "ana", body = "definitely ana" });

        var comment = await created.Content.ReadFromJsonAsync<CommentPayload>();
        Assert.Equal("ben", comment!.Author);
    }
}
