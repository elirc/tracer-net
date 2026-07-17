using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// Roadmap milestones: their lifecycle, and the progress they roll up from the
/// issues gathered under them.
/// </summary>
public class MilestonesApiTests : IClassFixture<TracerApiFactory>
{
    private readonly TracerApiFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _foreigner;

    public MilestonesApiTests(TracerApiFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateAdminClient();
        _foreigner = factory.CreateDesMemberClient();
    }

    private sealed record TeamPayload(Guid Id, string Key);
    private sealed record StatePayload(Guid Id, string Name, string Type);
    private sealed record ProjectPayload(Guid Id, Guid TeamId, string Name);
    private sealed record IssuePayload(Guid Id, string Identifier, Guid? MilestoneId, string State);
    private sealed record MilestonePayload(
        Guid Id, Guid TeamId, Guid ProjectId, string Name, string? Description,
        DateTimeOffset TargetDate, DateTimeOffset CreatedAt,
        int TotalIssues, int ScopeIssues, int CompletedIssues, double ProgressPercent, string Status);

    private async Task<(TeamPayload Team, List<StatePayload> States)> CreateTeamAsync(string name, string key)
    {
        var response = await _admin.PostAsJsonAsync("/api/teams", new { name, key });
        response.EnsureSuccessStatusCode();
        var team = (await response.Content.ReadFromJsonAsync<TeamPayload>())!;
        var states = (await _admin.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{team.Id}/states"))!;
        return (team, states);
    }

    private async Task<ProjectPayload> CreateProjectAsync(Guid teamId, string name)
    {
        var response = await _admin.PostAsJsonAsync($"/api/teams/{teamId}/projects", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectPayload>())!;
    }

    private async Task<MilestonePayload> CreateMilestoneAsync(Guid projectId, string name, DateTimeOffset targetDate)
    {
        var response = await _admin.PostAsJsonAsync($"/api/projects/{projectId}/milestones",
            new { name, targetDate });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MilestonePayload>())!;
    }

    private async Task<IssuePayload> CreateIssueAsync(Guid teamId, object body)
    {
        var response = await _admin.PostAsJsonAsync($"/api/teams/{teamId}/issues", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    private async Task<MilestonePayload> GetMilestoneAsync(Guid id) =>
        (await _admin.GetFromJsonAsync<MilestonePayload>($"/api/milestones/{id}"))!;

    private static readonly DateTimeOffset Future = DateTimeOffset.UtcNow.AddDays(30);
    private static readonly DateTimeOffset Past = DateTimeOffset.UtcNow.AddDays(-30);

    // ---- Lifecycle ----

    [Fact]
    public async Task A_new_milestone_starts_empty_and_upcoming()
    {
        var (team, _) = await CreateTeamAsync("Mile New", "MLN");
        var project = await CreateProjectAsync(team.Id, "Launch");

        var milestone = await CreateMilestoneAsync(project.Id, "v1", Future);

        Assert.Equal(project.Id, milestone.ProjectId);
        Assert.Equal(team.Id, milestone.TeamId);
        Assert.Equal(0, milestone.TotalIssues);
        Assert.Equal(0, milestone.ProgressPercent);
        Assert.Equal("Upcoming", milestone.Status);
    }

    [Fact]
    public async Task A_milestone_can_be_updated_and_deleted()
    {
        var (team, _) = await CreateTeamAsync("Mile Edit", "MLE");
        var project = await CreateProjectAsync(team.Id, "Launch");
        var milestone = await CreateMilestoneAsync(project.Id, "old", Future);

        var updated = await _admin.PutAsJsonAsync($"/api/milestones/{milestone.Id}",
            new { name = "new", description = "now with a description", targetDate = Future.AddDays(1) });
        updated.EnsureSuccessStatusCode();
        Assert.Equal("new", (await updated.Content.ReadFromJsonAsync<MilestonePayload>())!.Name);

        var deleted = await _admin.DeleteAsync($"/api/milestones/{milestone.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/api/milestones/{milestone.Id}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_milestone_releases_its_issues_rather_than_deleting_them()
    {
        var (team, _) = await CreateTeamAsync("Mile Release", "MLR");
        var project = await CreateProjectAsync(team.Id, "Launch");
        var milestone = await CreateMilestoneAsync(project.Id, "v1", Future);
        var issue = await CreateIssueAsync(team.Id, new { title = "under the milestone", milestoneId = milestone.Id });

        await _admin.DeleteAsync($"/api/milestones/{milestone.Id}");

        // The issue survives, just no longer on a roadmap.
        var after = (await _admin.GetFromJsonAsync<IssuePayload>($"/api/issues/{issue.Id}"))!;
        Assert.Null(after.MilestoneId);
    }

    // ---- Progress ----

    [Fact]
    public async Task Progress_rolls_up_from_the_issues_pointing_at_the_milestone()
    {
        var (team, states) = await CreateTeamAsync("Mile Progress", "MLP");
        var done = states.Single(s => s.Name == "Done").Id;
        var project = await CreateProjectAsync(team.Id, "Launch");
        var milestone = await CreateMilestoneAsync(project.Id, "v1", Future);

        await CreateIssueAsync(team.Id, new { title = "done one", milestoneId = milestone.Id, stateId = done });
        await CreateIssueAsync(team.Id, new { title = "done two", milestoneId = milestone.Id, stateId = done });
        await CreateIssueAsync(team.Id, new { title = "still open", milestoneId = milestone.Id });

        var rolled = await GetMilestoneAsync(milestone.Id);
        Assert.Equal(3, rolled.TotalIssues);
        Assert.Equal(2, rolled.CompletedIssues);
        Assert.Equal(66.7, rolled.ProgressPercent);
        Assert.Equal("Upcoming", rolled.Status);
    }

    [Fact]
    public async Task A_milestone_with_every_issue_done_is_complete_even_when_late()
    {
        var (team, states) = await CreateTeamAsync("Mile Late", "MLL");
        var done = states.Single(s => s.Name == "Done").Id;
        var project = await CreateProjectAsync(team.Id, "Launch");
        var milestone = await CreateMilestoneAsync(project.Id, "shipped late", Past);

        await CreateIssueAsync(team.Id, new { title = "finished", milestoneId = milestone.Id, stateId = done });

        var rolled = await GetMilestoneAsync(milestone.Id);
        Assert.Equal(100, rolled.ProgressPercent);
        Assert.Equal("Completed", rolled.Status);
    }

    [Fact]
    public async Task A_past_due_milestone_with_open_work_is_overdue()
    {
        var (team, _) = await CreateTeamAsync("Mile Over", "MLO");
        var project = await CreateProjectAsync(team.Id, "Launch");
        var milestone = await CreateMilestoneAsync(project.Id, "slipped", Past);
        await CreateIssueAsync(team.Id, new { title = "unfinished", milestoneId = milestone.Id });

        Assert.Equal("Overdue", (await GetMilestoneAsync(milestone.Id)).Status);
    }

    // ---- Roadmap ----

    [Fact]
    public async Task The_roadmap_lists_a_teams_milestones_in_target_date_order()
    {
        var (team, _) = await CreateTeamAsync("Mile Roadmap", "MLD");
        var project = await CreateProjectAsync(team.Id, "Launch");
        await CreateMilestoneAsync(project.Id, "third", Future.AddDays(20));
        await CreateMilestoneAsync(project.Id, "first", Future.AddDays(5));
        await CreateMilestoneAsync(project.Id, "second", Future.AddDays(10));

        var roadmap = (await _admin.GetFromJsonAsync<List<MilestonePayload>>($"/api/teams/{team.Id}/milestones"))!;

        Assert.Equal(["first", "second", "third"], roadmap.Select(m => m.Name).ToArray());
    }

    [Fact]
    public async Task The_roadmap_can_be_filtered_by_project_and_by_status()
    {
        var (team, states) = await CreateTeamAsync("Mile Filter", "MLF");
        var done = states.Single(s => s.Name == "Done").Id;
        var alpha = await CreateProjectAsync(team.Id, "Alpha");
        var beta = await CreateProjectAsync(team.Id, "Beta");

        var complete = await CreateMilestoneAsync(alpha.Id, "alpha done", Future);
        await CreateIssueAsync(team.Id, new { title = "a", milestoneId = complete.Id, stateId = done });
        await CreateMilestoneAsync(alpha.Id, "alpha open", Future);
        await CreateMilestoneAsync(beta.Id, "beta open", Future);

        var alphaOnly = (await _admin.GetFromJsonAsync<List<MilestonePayload>>(
            $"/api/teams/{team.Id}/milestones?projectId={alpha.Id}"))!;
        Assert.Equal(2, alphaOnly.Count);
        Assert.All(alphaOnly, m => Assert.Equal(alpha.Id, m.ProjectId));

        var completed = (await _admin.GetFromJsonAsync<List<MilestonePayload>>(
            $"/api/teams/{team.Id}/milestones?status=Completed"))!;
        Assert.Equal("alpha done", Assert.Single(completed).Name);
    }

    // ---- Validation and scope ----

    [Fact]
    public async Task An_issue_may_not_point_at_a_milestone_from_another_team()
    {
        var (mine, _) = await CreateTeamAsync("Mile Mine", "MMN");
        var (other, _) = await CreateTeamAsync("Mile Other", "MOT");
        var otherProject = await CreateProjectAsync(other.Id, "Elsewhere");
        var otherMilestone = await CreateMilestoneAsync(otherProject.Id, "not yours", Future);

        var response = await _admin.PostAsJsonAsync($"/api/teams/{mine.Id}/issues",
            new { title = "trespassing", milestoneId = otherMilestone.Id });

        // Same 400 an unknown project or cycle produces: a reference to something
        // outside this team is a bad request, not a domain-rule violation.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("milestone", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Creating_a_milestone_requires_a_name_and_a_target_date()
    {
        var (team, _) = await CreateTeamAsync("Mile Required", "MRQ");
        var project = await CreateProjectAsync(team.Id, "Launch");

        var noName = await _admin.PostAsJsonAsync($"/api/projects/{project.Id}/milestones", new { targetDate = Future });
        var noDate = await _admin.PostAsJsonAsync($"/api/projects/{project.Id}/milestones", new { name = "dateless" });

        // A missing target date must be a 400 for the omitted field, not a milestone
        // quietly dated 0001-01-01.
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noDate.StatusCode);
    }

    [Fact]
    public async Task A_foreigner_can_neither_read_nor_create_milestones()
    {
        var (team, _) = await CreateTeamAsync("Mile Guard", "MLG");
        var project = await CreateProjectAsync(team.Id, "Launch");
        var milestone = await CreateMilestoneAsync(project.Id, "v1", Future);

        Assert.Equal(HttpStatusCode.NotFound,
            (await _foreigner.GetAsync($"/api/teams/{team.Id}/milestones")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _foreigner.GetAsync($"/api/milestones/{milestone.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _foreigner.PostAsJsonAsync($"/api/projects/{project.Id}/milestones",
                new { name = "sneaky", targetDate = Future })).StatusCode);
    }

    [Fact]
    public async Task Milestones_require_a_credential()
    {
        var (team, _) = await CreateTeamAsync("Mile Anon", "MLA");
        var anonymous = _factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/teams/{team.Id}/milestones")).StatusCode);
    }
}
