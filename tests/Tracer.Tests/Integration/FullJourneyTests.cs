using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// Walks the whole product in one pass — team, project, cycle, label, issue,
/// comment, transitions, reordering, search, and the cycle roll-up — over HTTP.
/// The per-feature suites each prove one seam in isolation; this proves the
/// seams line up when a real client uses them in order.
/// </summary>
public class FullJourneyTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public FullJourneyTests(TracerApiFactory factory)
    {
        _client = factory.CreateAdminClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record StatePayload(Guid Id, string Name, string Type, int Position);
    private sealed record ProjectPayload(Guid Id, Guid TeamId, string Name, string? Description);
    private sealed record LabelPayload(Guid Id, string Name, string Color);
    private sealed record CyclePayload(Guid Id, int Number, string? Name, string Status);
    private sealed record CommentPayload(Guid Id, Guid IssueId, string Author, string Body);
    private sealed record SummaryPayload(
        int TotalIssues, int ScopeIssues, int CompletedIssues, int InProgressIssues,
        int CanceledIssues, int ScopeEstimate, int CompletedEstimate, double ProgressPercent);
    private sealed record IssuePayload(
        Guid Id, Guid TeamId, string Identifier, int Number, string Title, string? Description,
        string Priority, int? Estimate, string? Assignee, Guid StateId, string State,
        Guid? ProjectId, Guid? CycleId, double Position, List<LabelPayload> Labels,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record PagePayload(List<IssuePayload> Items, int Page, int PageSize, int Total, int TotalPages);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"expected {expected} but got {response.StatusCode}: {body}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task A_team_can_plan_a_cycle_work_an_issue_to_done_and_find_it_again()
    {
        // 1. A new team arrives with the default workflow already in place.
        var team = await ReadAsync<TeamPayload>(
            await _client.PostAsJsonAsync("/api/teams", new { name = "Voyage", key = "VOY" }),
            HttpStatusCode.Created);

        var states = (await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{team.Id}/states"))!;
        Assert.Equal(["Backlog", "Todo", "In Progress", "Done", "Canceled"],
            states.OrderBy(s => s.Position).Select(s => s.Name).ToArray());

        // 2. They set up a project, a label, and the cycle they will plan into.
        var project = await ReadAsync<ProjectPayload>(
            await _client.PostAsJsonAsync($"/api/teams/{team.Id}/projects",
                new { name = "Launch", description = "Everything needed to ship v1." }),
            HttpStatusCode.Created);

        var label = await ReadAsync<LabelPayload>(
            await _client.PostAsJsonAsync($"/api/teams/{team.Id}/labels", new { name = "launch-blocker", color = "#eb5757" }),
            HttpStatusCode.Created);

        var now = DateTimeOffset.UtcNow;
        var cycle = await ReadAsync<CyclePayload>(
            await _client.PostAsJsonAsync($"/api/teams/{team.Id}/cycles",
                new { name = "Launch cycle", startsAt = now.AddDays(-1), endsAt = now.AddDays(13) }),
            HttpStatusCode.Created);
        Assert.Equal("Active", cycle.Status);

        // 3. Two issues are filed into the project and scheduled into the cycle.
        var feature = await ReadAsync<IssuePayload>(
            await _client.PostAsJsonAsync($"/api/teams/{team.Id}/issues", new
            {
                title = "Ship the onboarding flow",
                description = "New users need a guided first run.",
                priority = "High",
                estimate = 5,
                assignee = "ana",
                projectId = project.Id,
                cycleId = cycle.Id,
            }),
            HttpStatusCode.Created);

        Assert.Equal("VOY-1", feature.Identifier);
        Assert.Equal("Backlog", feature.State);
        Assert.Equal(cycle.Id, feature.CycleId);
        Assert.Equal(project.Id, feature.ProjectId);
        Assert.Equal("ana", feature.Assignee);

        var chore = await ReadAsync<IssuePayload>(
            await _client.PostAsJsonAsync($"/api/teams/{team.Id}/issues", new
            {
                title = "Update the changelog",
                priority = "Low",
                estimate = 1,
                assignee = "ben",
                projectId = project.Id,
                cycleId = cycle.Id,
            }),
            HttpStatusCode.Created);
        Assert.Equal("VOY-2", chore.Identifier);

        // 4. The blocker gets labelled and discussed.
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PutAsync($"/api/issues/{feature.Id}/labels/{label.Id}", null)).StatusCode);

        await ReadAsync<CommentPayload>(
            await _client.PostAsJsonAsync($"/api/issues/{feature.Id}/comments",
                new { body = "Blocked on the copy review." }),
            HttpStatusCode.Created);

        var labelled = await _client.GetFromJsonAsync<IssuePayload>($"/api/issues/{feature.Id}");
        Assert.Contains(labelled!.Labels, l => l.Name == "launch-blocker");

        // 5. Reordering puts the blocker at the top of the backlog.
        await ReadAsync<IssuePayload>(
            await _client.PostAsJsonAsync($"/api/issues/{feature.Id}/reorder", new { beforeIssueId = chore.Id }),
            HttpStatusCode.OK);

        var backlogOrder = (await _client.GetListAsync<IssuePayload>($"/api/teams/{team.Id}/issues"))!
            .Where(i => i.State == "Backlog")
            .OrderBy(i => i.Position)
            .Select(i => i.Identifier)
            .ToList();
        Assert.Equal(["VOY-1", "VOY-2"], backlogOrder);

        // 6. The workflow is enforced on the way to Done: no shortcuts.
        var shortcut = await _client.PostAsJsonAsync($"/api/issues/{feature.Id}/transitions",
            new { stateId = states.Single(s => s.Name == "Done").Id });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, shortcut.StatusCode);

        foreach (var step in new[] { "Todo", "In Progress", "Done" })
        {
            await ReadAsync<IssuePayload>(
                await _client.PostAsJsonAsync($"/api/issues/{feature.Id}/transitions",
                    new { stateId = states.Single(s => s.Name == step).Id }),
                HttpStatusCode.OK);
        }

        var finished = await _client.GetFromJsonAsync<IssuePayload>($"/api/issues/{feature.Id}");
        Assert.Equal("Done", finished!.State);

        // 7. The cycle roll-up reflects the work that actually landed.
        var summary = await _client.GetFromJsonAsync<SummaryPayload>($"/api/cycles/{cycle.Id}/summary");
        Assert.Equal(2, summary!.TotalIssues);
        Assert.Equal(2, summary.ScopeIssues);
        Assert.Equal(1, summary.CompletedIssues);
        Assert.Equal(6, summary.ScopeEstimate);
        Assert.Equal(5, summary.CompletedEstimate);
        Assert.Equal(50, summary.ProgressPercent);

        // 8. Every filter the issue was created with finds it again.
        foreach (var filter in new[]
                 {
                     $"teamId={team.Id}&q=onboarding",
                     $"teamId={team.Id}&assignee=ana",
                     $"projectId={project.Id}&priority=High",
                     $"labelId={label.Id}",
                     $"cycleId={cycle.Id}&stateId={states.Single(s => s.Name == "Done").Id}",
                 })
        {
            var page = await _client.GetFromJsonAsync<PagePayload>($"/api/issues?{filter}");
            Assert.Equal("VOY-1", Assert.Single(page!.Items).Identifier);
        }

        // 9. Search still sees both issues for the team, newest activity first.
        var everything = await _client.GetFromJsonAsync<PagePayload>($"/api/issues?teamId={team.Id}&sort=Updated&order=Desc");
        Assert.Equal(2, everything!.Total);
        Assert.Equal("VOY-1", everything.Items.First().Identifier); // most recently transitioned

        // 10. Deleting the cycle releases its issues without losing them.
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/cycles/{cycle.Id}")).StatusCode);
        var released = await _client.GetFromJsonAsync<IssuePayload>($"/api/issues/{feature.Id}");
        Assert.Null(released!.CycleId);
        Assert.Equal("Done", released.State);
    }

    [Fact]
    public async Task Seed_data_is_present_and_coherent_over_the_api()
    {
        var teams = (await _client.GetListAsync<TeamPayload>("/api/teams"))!;
        var eng = teams.Single(t => t.Key == "ENG");
        var des = teams.Single(t => t.Key == "DES");
        Assert.Equal("Engineering", eng.Name);
        Assert.Equal("Design", des.Name);

        // Both teams start on the default five-state workflow.
        foreach (var team in new[] { eng, des })
        {
            var states = (await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{team.Id}/states"))!;
            Assert.Equal(["Backlog", "Todo", "In Progress", "Done", "Canceled"],
                states.OrderBy(s => s.Position).Select(s => s.Name).ToArray());
        }

        var engIssues = (await _client.GetListAsync<IssuePayload>($"/api/teams/{eng.Id}/issues"))!;
        Assert.Equal(["ENG-1", "ENG-2", "ENG-3", "ENG-4"], engIssues.Select(i => i.Identifier).Order().ToArray());

        // The samples exercise the interesting shapes: labels, comments, a
        // project, an assignee, a cycle, and an unestimated/unassigned issue.
        Assert.Contains(engIssues, i => i.Labels.Any(l => l.Name == "bug"));
        Assert.Contains(engIssues, i => i.Assignee == "ana");
        Assert.Contains(engIssues, i => i.Assignee is null);
        Assert.Contains(engIssues, i => i.ProjectId is not null);
        Assert.Contains(engIssues, i => i.CycleId is not null);
        Assert.All(engIssues, i => Assert.Equal(eng.Id, i.TeamId));

        var projects = (await _client.GetListAsync<ProjectPayload>($"/api/teams/{eng.Id}/projects"))!;
        Assert.Equal(["Performance", "Public API"], projects.Select(p => p.Name).Order().ToArray());

        var labels = (await _client.GetListAsync<LabelPayload>($"/api/teams/{eng.Id}/labels"))!;
        Assert.Equal(["bug", "chore", "feature"], labels.Select(l => l.Name).Order().ToArray());

        // The seeded cycles are positioned around today, so one is live and one
        // is ahead of it rather than both drifting into the past.
        var cycles = (await _client.GetFromJsonAsync<List<CyclePayload>>($"/api/teams/{eng.Id}/cycles"))!;
        Assert.Equal(2, cycles.Count);
        Assert.Equal("Active", cycles.Single(c => c.Number == 1).Status);
        Assert.Equal("Upcoming", cycles.Single(c => c.Number == 2).Status);

        // The active cycle has real, measurable progress to demonstrate.
        var active = cycles.Single(c => c.Status == "Active");
        var summary = await _client.GetFromJsonAsync<SummaryPayload>($"/api/cycles/{active.Id}/summary");
        Assert.Equal(3, summary!.TotalIssues);
        Assert.Equal(1, summary.CompletedIssues);
        Assert.True(summary.ProgressPercent > 0);

        var comments = await _client.GetListAsync<CommentPayload>(
            $"/api/issues/{engIssues.Single(i => i.Identifier == "ENG-1").Id}/comments");
        Assert.Equal(2, comments.Count);
    }
}
