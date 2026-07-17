using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// Delivery metrics: velocity across cycles, a cycle's burndown, and throughput
/// and cycle-time reconstructed from the activity spine.
/// </summary>
public class MetricsApiTests : IClassFixture<TracerApiFactory>
{
    private readonly TracerApiFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _foreigner;

    public MetricsApiTests(TracerApiFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateAdminClient();
        _foreigner = factory.CreateDesMemberClient();
    }

    private sealed record TeamPayload(Guid Id, string Key);
    private sealed record StatePayload(Guid Id, string Name, string Type);
    private sealed record ProjectPayload(Guid Id, string Name);
    private sealed record IssuePayload(Guid Id, Guid StateId, string State);
    private sealed record CyclePayload(Guid Id, int Number, string Status);

    private sealed record VelocityCycle(Guid CycleId, int Number, int CommittedPoints, int CompletedPoints, int CompletedIssues);
    private sealed record Velocity(Guid TeamId, List<VelocityCycle> Cycles, double AverageVelocity);
    private sealed record BurndownPoint(DateTimeOffset Date, int ScopePoints, int CompletedPoints, int RemainingPoints, double IdealRemaining);
    private sealed record Burndown(Guid CycleId, int ScopePoints, int ScopeAddedPoints, int CompletedPoints, List<BurndownPoint> Series);
    private sealed record ThroughputBucket(DateTimeOffset BucketStart, int Completed);
    private sealed record Throughput(Guid TeamId, string Interval, int TotalCompleted, List<ThroughputBucket> Buckets);
    private sealed record CycleTime(Guid TeamId, int CompletedIssues, double? P50Hours, double? P75Hours, double? P90Hours);

    private async Task<(TeamPayload Team, Dictionary<string, Guid> States)> CreateTeamAsync(string name, string key)
    {
        var response = await _admin.PostAsJsonAsync("/api/teams", new { name, key });
        response.EnsureSuccessStatusCode();
        var team = (await response.Content.ReadFromJsonAsync<TeamPayload>())!;
        var states = (await _admin.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{team.Id}/states"))!;
        return (team, states.ToDictionary(s => s.Name, s => s.Id));
    }

    private async Task<ProjectPayload> CreateProjectAsync(Guid teamId, string name)
    {
        var response = await _admin.PostAsJsonAsync($"/api/teams/{teamId}/projects", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectPayload>())!;
    }

    private async Task<CyclePayload> CreateCycleAsync(Guid teamId, DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        var response = await _admin.PostAsJsonAsync($"/api/teams/{teamId}/cycles",
            new { name = $"cycle {startsAt:MMdd}", startsAt, endsAt });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CyclePayload>())!;
    }

    private async Task<IssuePayload> CreateIssueAsync(Guid teamId, object body)
    {
        var response = await _admin.PostAsJsonAsync($"/api/teams/{teamId}/issues", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    /// <summary>Walks an issue Backlog -> In Progress -> Done through the real endpoint, so the state-change activities exist.</summary>
    private async Task DriveToDoneAsync(Guid issueId, Dictionary<string, Guid> states)
    {
        foreach (var name in new[] { "In Progress", "Done" })
        {
            var response = await _admin.PostAsJsonAsync($"/api/issues/{issueId}/transitions", new { stateId = states[name] });
            response.EnsureSuccessStatusCode();
        }
    }

    // ---- Velocity ----

    [Fact]
    public async Task Velocity_reports_completed_points_for_completed_cycles_only()
    {
        var (team, states) = await CreateTeamAsync("Velo Basic", "VLB");
        var now = DateTimeOffset.UtcNow;
        var doneState = states["Done"];

        var finished = await CreateCycleAsync(team.Id, now.AddDays(-30), now.AddDays(-16));
        await CreateIssueAsync(team.Id, new { title = "d1", estimate = 5, cycleId = finished.Id, stateId = doneState });
        await CreateIssueAsync(team.Id, new { title = "d2", estimate = 3, cycleId = finished.Id, stateId = doneState });
        await CreateIssueAsync(team.Id, new { title = "open", estimate = 2, cycleId = finished.Id });

        // An in-flight cycle: its work must not count toward velocity yet.
        var active = await CreateCycleAsync(team.Id, now.AddDays(-3), now.AddDays(11));
        await CreateIssueAsync(team.Id, new { title = "current", estimate = 8, cycleId = active.Id, stateId = doneState });

        var velocity = (await _admin.GetFromJsonAsync<Velocity>($"/api/teams/{team.Id}/metrics/velocity"))!;

        var only = Assert.Single(velocity.Cycles);
        Assert.Equal(finished.Id, only.CycleId);
        Assert.Equal(10, only.CommittedPoints); // 5 + 3 + 2 (open still committed)
        Assert.Equal(8, only.CompletedPoints); // 5 + 3
        Assert.Equal(2, only.CompletedIssues);
        Assert.Equal(8, velocity.AverageVelocity);
    }

    [Fact]
    public async Task Velocity_averages_only_the_last_n_cycles_when_asked()
    {
        var (team, states) = await CreateTeamAsync("Velo Take", "VLT");
        var now = DateTimeOffset.UtcNow;
        var doneState = states["Done"];

        // Three completed cycles with completed points 2, 4, 9.
        foreach (var (offset, points) in new[] { (-60, 2), (-45, 4), (-30, 9) })
        {
            var cycle = await CreateCycleAsync(team.Id, now.AddDays(offset), now.AddDays(offset + 14));
            await CreateIssueAsync(team.Id, new { title = $"c{offset}", estimate = points, cycleId = cycle.Id, stateId = doneState });
        }

        var last2 = (await _admin.GetFromJsonAsync<Velocity>($"/api/teams/{team.Id}/metrics/velocity?take=2"))!;

        Assert.Equal(2, last2.Cycles.Count);
        Assert.Equal(6.5, last2.AverageVelocity); // (4 + 9) / 2
    }

    [Fact]
    public async Task Velocity_of_a_foreign_team_is_404() =>
        Assert.Equal(HttpStatusCode.NotFound,
            (await _foreigner.GetAsync($"/api/teams/{(await SeedEngId())}/metrics/velocity")).StatusCode);

    // ---- Burndown ----

    [Fact]
    public async Task Burndown_reports_scope_and_completed_points_with_a_daily_series()
    {
        var (team, states) = await CreateTeamAsync("Burn Basic", "BNB");
        var now = DateTimeOffset.UtcNow;
        var cycle = await CreateCycleAsync(team.Id, now.AddDays(-3), now.AddDays(11));

        var done = await CreateIssueAsync(team.Id, new { title = "done", estimate = 5, cycleId = cycle.Id });
        await DriveToDoneAsync(done.Id, states);
        await CreateIssueAsync(team.Id, new { title = "open", estimate = 3, cycleId = cycle.Id });
        // Canceled work is out of scope entirely.
        var canceled = await CreateIssueAsync(team.Id, new { title = "dropped", estimate = 4, cycleId = cycle.Id });
        await _admin.PostAsJsonAsync($"/api/issues/{canceled.Id}/transitions", new { stateId = states["Canceled"] });

        var burndown = (await _admin.GetFromJsonAsync<Burndown>($"/api/cycles/{cycle.Id}/burndown"))!;

        Assert.Equal(8, burndown.ScopePoints); // 5 + 3, not the canceled 4
        Assert.Equal(5, burndown.CompletedPoints);
        Assert.NotEmpty(burndown.Series);
        // The remaining line ends at what is still open.
        Assert.Equal(3, burndown.Series[^1].RemainingPoints);
    }

    [Fact]
    public async Task Burndown_of_a_foreign_cycle_is_404()
    {
        var (team, _) = await CreateTeamAsync("Burn Guard", "BNG");
        var now = DateTimeOffset.UtcNow;
        var cycle = await CreateCycleAsync(team.Id, now.AddDays(-1), now.AddDays(13));

        Assert.Equal(HttpStatusCode.NotFound, (await _foreigner.GetAsync($"/api/cycles/{cycle.Id}/burndown")).StatusCode);
    }

    // ---- Throughput ----

    [Fact]
    public async Task Throughput_counts_issues_completed_in_the_window()
    {
        var (team, states) = await CreateTeamAsync("Thru Basic", "THB");

        var a = await CreateIssueAsync(team.Id, new { title = "a" });
        var b = await CreateIssueAsync(team.Id, new { title = "b" });
        await CreateIssueAsync(team.Id, new { title = "never done" });
        await DriveToDoneAsync(a.Id, states);
        await DriveToDoneAsync(b.Id, states);

        var throughput = (await _admin.GetFromJsonAsync<Throughput>($"/api/teams/{team.Id}/metrics/throughput"))!;

        Assert.Equal(2, throughput.TotalCompleted);
        Assert.Equal(2, throughput.Buckets.Sum(bucket => bucket.Completed));
    }

    [Fact]
    public async Task Throughput_excludes_completions_outside_the_window()
    {
        var (team, states) = await CreateTeamAsync("Thru Window", "THW");
        var issue = await CreateIssueAsync(team.Id, new { title = "done now" });
        await DriveToDoneAsync(issue.Id, states);

        var now = DateTimeOffset.UtcNow;
        // Escape the timestamps: an ISO offset carries a '+' that a query string
        // would otherwise read as a space.
        var from = Uri.EscapeDataString(now.AddDays(1).ToString("o"));
        var to = Uri.EscapeDataString(now.AddDays(2).ToString("o"));
        var future = (await _admin.GetFromJsonAsync<Throughput>(
            $"/api/teams/{team.Id}/metrics/throughput?from={from}&to={to}"))!;

        Assert.Equal(0, future.TotalCompleted);
    }

    [Fact]
    public async Task Throughput_can_bucket_by_week()
    {
        var (team, states) = await CreateTeamAsync("Thru Week", "THK");
        var issue = await CreateIssueAsync(team.Id, new { title = "shipped" });
        await DriveToDoneAsync(issue.Id, states);

        var weekly = (await _admin.GetFromJsonAsync<Throughput>(
            $"/api/teams/{team.Id}/metrics/throughput?interval=Week"))!;

        Assert.Equal("Week", weekly.Interval);
        Assert.Equal(1, weekly.TotalCompleted);
    }

    // ---- Cycle time ----

    [Fact]
    public async Task Cycle_time_reports_percentiles_over_completed_issues()
    {
        var (team, states) = await CreateTeamAsync("CT Basic", "CTB");
        foreach (var title in new[] { "one", "two", "three" })
        {
            var issue = await CreateIssueAsync(team.Id, new { title });
            await DriveToDoneAsync(issue.Id, states);
        }

        var cycleTime = (await _admin.GetFromJsonAsync<CycleTime>($"/api/teams/{team.Id}/metrics/cycle-time"))!;

        Assert.Equal(3, cycleTime.CompletedIssues);
        Assert.NotNull(cycleTime.P50Hours);
        Assert.NotNull(cycleTime.P90Hours);
        Assert.True(cycleTime.P50Hours >= 0);
    }

    [Fact]
    public async Task Cycle_time_over_a_window_with_no_completions_is_a_null_percentile_not_a_zero()
    {
        var (team, _) = await CreateTeamAsync("CT Empty", "CTE");
        await CreateIssueAsync(team.Id, new { title = "still open" });

        var cycleTime = (await _admin.GetFromJsonAsync<CycleTime>($"/api/teams/{team.Id}/metrics/cycle-time"))!;

        Assert.Equal(0, cycleTime.CompletedIssues);
        Assert.Null(cycleTime.P50Hours);
        Assert.Null(cycleTime.P90Hours);
    }

    [Fact]
    public async Task Cycle_time_can_be_filtered_to_a_project()
    {
        var (team, states) = await CreateTeamAsync("CT Project", "CTP");
        var project = await CreateProjectAsync(team.Id, "Focus");

        var inProject = await CreateIssueAsync(team.Id, new { title = "in", projectId = project.Id });
        await DriveToDoneAsync(inProject.Id, states);
        var elsewhere = await CreateIssueAsync(team.Id, new { title = "out" });
        await DriveToDoneAsync(elsewhere.Id, states);

        var scoped = (await _admin.GetFromJsonAsync<CycleTime>(
            $"/api/teams/{team.Id}/metrics/cycle-time?projectId={project.Id}"))!;

        Assert.Equal(1, scoped.CompletedIssues);
    }

    [Fact]
    public async Task Metrics_require_a_credential()
    {
        var anonymous = _factory.CreateAnonymousClient();
        var engId = await SeedEngId();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/teams/{engId}/metrics/throughput")).StatusCode);
    }

    private async Task<Guid> SeedEngId()
    {
        var teams = await _admin.GetFromJsonAsync<List<TeamPayload>>("/api/teams");
        return teams!.Single(t => t.Key == "ENG").Id;
    }
}
