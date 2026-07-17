using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

public class CyclesApiTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public CyclesApiTests(TracerApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record StatePayload(Guid Id, string Name, string Type, int Position);
    private sealed record IssuePayload(Guid Id, string Identifier, Guid StateId, string State, Guid? CycleId);
    private sealed record CyclePayload(
        Guid Id, Guid TeamId, int Number, string? Name,
        DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status);
    private sealed record SummaryPayload(
        Guid Id, int Number, string Status, int TotalIssues, int ScopeIssues, int CompletedIssues,
        int InProgressIssues, int CanceledIssues, int ScopeEstimate, int CompletedEstimate, double ProgressPercent);

    private async Task<TeamPayload> CreateTeamAsync(string name, string key)
    {
        var response = await _client.PostAsJsonAsync("/api/teams", new { name, key });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TeamPayload>())!;
    }

    private async Task<CyclePayload> CreateCycleAsync(Guid teamId, string name, DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        var response = await _client.PostAsJsonAsync($"/api/teams/{teamId}/cycles", new { name, startsAt, endsAt });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CyclePayload>())!;
    }

    [Fact]
    public async Task Create_assigns_sequential_numbers_per_team()
    {
        var team = await CreateTeamAsync("Cycle Numbers", "CYN");
        var now = DateTimeOffset.UtcNow;

        var first = await CreateCycleAsync(team.Id, "Cycle 1", now, now.AddDays(14));
        var second = await CreateCycleAsync(team.Id, "Cycle 2", now.AddDays(14), now.AddDays(28));

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
    }

    [Fact]
    public async Task Status_is_derived_from_the_dates()
    {
        var team = await CreateTeamAsync("Cycle Status", "CYS");
        var now = DateTimeOffset.UtcNow;

        var completed = await CreateCycleAsync(team.Id, "past", now.AddDays(-28), now.AddDays(-14));
        var active = await CreateCycleAsync(team.Id, "present", now.AddDays(-7), now.AddDays(7));
        var upcoming = await CreateCycleAsync(team.Id, "future", now.AddDays(14), now.AddDays(28));

        Assert.Equal("Completed", completed.Status);
        Assert.Equal("Active", active.Status);
        Assert.Equal("Upcoming", upcoming.Status);
    }

    [Fact]
    public async Task List_filters_by_status()
    {
        var team = await CreateTeamAsync("Cycle Filter", "CYF");
        var now = DateTimeOffset.UtcNow;
        await CreateCycleAsync(team.Id, "past", now.AddDays(-28), now.AddDays(-14));
        await CreateCycleAsync(team.Id, "present", now.AddDays(-7), now.AddDays(7));
        await CreateCycleAsync(team.Id, "future", now.AddDays(14), now.AddDays(28));

        var active = await _client.GetFromJsonAsync<List<CyclePayload>>($"/api/teams/{team.Id}/cycles?status=Active");
        var all = await _client.GetFromJsonAsync<List<CyclePayload>>($"/api/teams/{team.Id}/cycles");

        Assert.Equal("present", Assert.Single(active!).Name);
        Assert.Equal(3, all!.Count);
        Assert.Equal([1, 2, 3], all.Select(c => c.Number).ToArray());
    }

    [Fact]
    public async Task Create_with_end_before_start_returns_422()
    {
        var team = await CreateTeamAsync("Cycle Backwards", "CYB");
        var now = DateTimeOffset.UtcNow;

        var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/cycles",
            new { name = "backwards", startsAt = now.AddDays(14), endsAt = now });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("must end after it starts", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_overlapping_cycle_returns_409()
    {
        var team = await CreateTeamAsync("Cycle Overlap", "CYO");
        var now = DateTimeOffset.UtcNow;
        await CreateCycleAsync(team.Id, "first", now, now.AddDays(14));

        var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/cycles",
            new { name = "clashing", startsAt = now.AddDays(7), endsAt = now.AddDays(21) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Overlapping cycle", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cycles_that_touch_at_a_boundary_are_allowed()
    {
        var team = await CreateTeamAsync("Cycle Touch", "CYT");
        var now = DateTimeOffset.UtcNow;
        var boundary = now.AddDays(14);
        await CreateCycleAsync(team.Id, "first", now, boundary);

        var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/cycles",
            new { name = "second", startsAt = boundary, endsAt = boundary.AddDays(14) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Overlap_is_scoped_to_one_team()
    {
        var teamA = await CreateTeamAsync("Cycle Team A", "CTA");
        var teamB = await CreateTeamAsync("Cycle Team B", "CTB");
        var now = DateTimeOffset.UtcNow;
        await CreateCycleAsync(teamA.Id, "same dates", now, now.AddDays(14));

        var response = await _client.PostAsJsonAsync($"/api/teams/{teamB.Id}/cycles",
            new { name = "same dates", startsAt = now, endsAt = now.AddDays(14) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Update_can_move_a_cycle_but_still_rejects_overlaps()
    {
        var team = await CreateTeamAsync("Cycle Update", "CYU");
        var now = DateTimeOffset.UtcNow;
        var first = await CreateCycleAsync(team.Id, "first", now, now.AddDays(14));
        var second = await CreateCycleAsync(team.Id, "second", now.AddDays(14), now.AddDays(28));

        // Moving a cycle onto itself must not count as an overlap.
        var shifted = await _client.PutAsJsonAsync($"/api/cycles/{second.Id}",
            new { name = "second, shifted", startsAt = now.AddDays(15), endsAt = now.AddDays(29) });
        Assert.Equal(HttpStatusCode.OK, shifted.StatusCode);
        var updated = await shifted.Content.ReadFromJsonAsync<CyclePayload>();
        Assert.Equal("second, shifted", updated!.Name);

        var clashing = await _client.PutAsJsonAsync($"/api/cycles/{second.Id}",
            new { name = "second", startsAt = first.StartsAt.AddDays(1), endsAt = now.AddDays(29) });
        Assert.Equal(HttpStatusCode.Conflict, clashing.StatusCode);
    }

    [Fact]
    public async Task Summary_reports_progress_and_excludes_canceled_work_from_scope()
    {
        var team = await CreateTeamAsync("Cycle Summary", "CSU");
        var states = (await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{team.Id}/states"))!;
        var now = DateTimeOffset.UtcNow;
        var cycle = await CreateCycleAsync(team.Id, "measured", now.AddDays(-1), now.AddDays(13));

        async Task<IssuePayload> AddIssueAsync(string title, int estimate, string stateName)
        {
            var state = states.Single(s => s.Name == stateName);
            var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/issues",
                new { title, estimate, cycleId = cycle.Id, stateId = state.Id });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<IssuePayload>())!;
        }

        await AddIssueAsync("done work", 3, "Done");
        await AddIssueAsync("in flight", 5, "In Progress");
        await AddIssueAsync("not started", 2, "Todo");
        await AddIssueAsync("called off", 8, "Canceled");

        var summary = await _client.GetFromJsonAsync<SummaryPayload>($"/api/cycles/{cycle.Id}/summary");

        Assert.NotNull(summary);
        Assert.Equal("Active", summary.Status);
        Assert.Equal(4, summary.TotalIssues);
        Assert.Equal(3, summary.ScopeIssues); // the canceled issue drops out of scope
        Assert.Equal(1, summary.CompletedIssues);
        Assert.Equal(1, summary.InProgressIssues);
        Assert.Equal(1, summary.CanceledIssues);
        Assert.Equal(10, summary.ScopeEstimate); // 3 + 5 + 2, not the canceled 8
        Assert.Equal(3, summary.CompletedEstimate);
        Assert.Equal(33.3, summary.ProgressPercent); // 1 of 3 in scope
    }

    [Fact]
    public async Task Summary_of_an_empty_cycle_reports_zero_progress()
    {
        var team = await CreateTeamAsync("Cycle Empty", "CYE");
        var now = DateTimeOffset.UtcNow;
        var cycle = await CreateCycleAsync(team.Id, "empty", now, now.AddDays(14));

        var summary = await _client.GetFromJsonAsync<SummaryPayload>($"/api/cycles/{cycle.Id}/summary");

        Assert.Equal(0, summary!.TotalIssues);
        Assert.Equal(0, summary.ProgressPercent);
    }

    [Fact]
    public async Task Issue_can_be_assigned_to_and_cleared_from_a_cycle()
    {
        var team = await CreateTeamAsync("Cycle Assign", "CYA");
        var now = DateTimeOffset.UtcNow;
        var cycle = await CreateCycleAsync(team.Id, "target", now, now.AddDays(14));

        var created = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/issues", new { title = "unscheduled" });
        var issue = (await created.Content.ReadFromJsonAsync<IssuePayload>())!;
        Assert.Null(issue.CycleId);

        var assigned = await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "unscheduled", priority = "None", cycleId = cycle.Id });
        var scheduled = await assigned.Content.ReadFromJsonAsync<IssuePayload>();
        Assert.Equal(cycle.Id, scheduled!.CycleId);

        var cleared = await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "unscheduled", priority = "None", cycleId = (Guid?)null });
        var unscheduled = await cleared.Content.ReadFromJsonAsync<IssuePayload>();
        Assert.Null(unscheduled!.CycleId);
    }

    [Fact]
    public async Task Assigning_an_issue_to_another_teams_cycle_returns_400()
    {
        var teamA = await CreateTeamAsync("Cycle Foreign A", "CFA");
        var teamB = await CreateTeamAsync("Cycle Foreign B", "CFB");
        var now = DateTimeOffset.UtcNow;
        var cycleB = await CreateCycleAsync(teamB.Id, "theirs", now, now.AddDays(14));

        var response = await _client.PostAsJsonAsync($"/api/teams/{teamA.Id}/issues",
            new { title = "cross-team cycle", cycleId = cycleB.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_cycle_unassigns_its_issues_without_deleting_them()
    {
        var team = await CreateTeamAsync("Cycle Delete", "CYD");
        var now = DateTimeOffset.UtcNow;
        var cycle = await CreateCycleAsync(team.Id, "doomed", now, now.AddDays(14));
        var created = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/issues",
            new { title = "survivor", cycleId = cycle.Id });
        var issue = (await created.Content.ReadFromJsonAsync<IssuePayload>())!;

        var deleted = await _client.DeleteAsync($"/api/cycles/{cycle.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var survivor = await _client.GetFromJsonAsync<IssuePayload>($"/api/issues/{issue.Id}");
        Assert.NotNull(survivor);
        Assert.Null(survivor.CycleId);
    }

    [Fact]
    public async Task Unknown_cycle_returns_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/cycles/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/cycles/{Guid.NewGuid()}/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/cycles/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Cycles_for_an_unknown_team_return_404()
    {
        var response = await _client.GetAsync($"/api/teams/{Guid.NewGuid()}/cycles");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Seeded_engineering_team_has_a_current_and_a_next_cycle()
    {
        var teams = await _client.GetFromJsonAsync<List<TeamPayload>>("/api/teams");
        var eng = teams!.Single(t => t.Key == "ENG");

        var cycles = await _client.GetFromJsonAsync<List<CyclePayload>>($"/api/teams/{eng.Id}/cycles");

        Assert.Equal(2, cycles!.Count);
        Assert.Contains(cycles, c => c.Status == "Active");
        Assert.Contains(cycles, c => c.Status == "Upcoming");
    }
}
