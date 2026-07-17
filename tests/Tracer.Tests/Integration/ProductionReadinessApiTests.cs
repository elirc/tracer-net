using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// Sprint 15 production-readiness guarantees: bounded list reads, optimistic
/// concurrency surfaced as 409, and write-only rate limiting.
/// </summary>
public class ProductionReadinessApiTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public ProductionReadinessApiTests(TracerApiFactory factory)
    {
        _client = factory.CreateAdminClient();
    }

    [Fact]
    public async Task List_endpoint_returns_the_paged_envelope()
    {
        var page = await _client.GetFromJsonAsync<Paged<TeamPayload>>("/api/teams?page=1&pageSize=1");

        Assert.NotNull(page);
        Assert.Equal(1, page.Page);
        Assert.Equal(1, page.PageSize);
        Assert.True(page.Total >= 2); // ENG and DES are seeded
        Assert.Single(page.Items);
        Assert.True(page.TotalPages >= 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public async Task Page_size_is_bounded_to_1_through_100(int pageSize)
    {
        var response = await _client.GetAsync($"/api/teams?pageSize={pageSize}");

        // The 1..100 cap is what makes "list" a bounded read; an out-of-range
        // pageSize is a 400, not a silently unbounded query.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Paging_a_bounded_endpoint_does_not_repeat_or_skip_rows()
    {
        var eng = await EngTeamAsync();

        // Fill a fresh project list past a page boundary.
        for (var i = 0; i < 7; i++)
        {
            var created = await _client.PostAsJsonAsync($"/api/teams/{eng.Id}/projects",
                new { name = $"Paged project {i:D2}" });
            created.EnsureSuccessStatusCode();
        }

        var seen = new List<Guid>();
        Paged<ProjectPayload>? page;
        var pageNumber = 1;
        do
        {
            page = await _client.GetFromJsonAsync<Paged<ProjectPayload>>(
                $"/api/teams/{eng.Id}/projects?page={pageNumber}&pageSize=3");
            seen.AddRange(page!.Items.Select(p => p.Id));
            pageNumber++;
        }
        while (pageNumber <= page.TotalPages);

        Assert.Equal(seen.Count, seen.Distinct().Count()); // no row seen twice
        Assert.Equal(page.Total, seen.Count); // and none skipped
    }

    [Fact]
    public async Task Issue_carries_a_version_token_that_rotates_on_update()
    {
        var issue = await CreateIssueAsync("Concurrency subject");

        var updated = await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "Concurrency subject v2", priority = "High" });
        updated.EnsureSuccessStatusCode();
        var afterUpdate = (await updated.Content.ReadFromJsonAsync<IssuePayload>())!;

        Assert.NotEqual(Guid.Empty, issue.Version);
        Assert.NotEqual(issue.Version, afterUpdate.Version);
    }

    [Fact]
    public async Task Update_with_a_stale_version_is_a_409()
    {
        var issue = await CreateIssueAsync("Lost update subject");
        var staleVersion = issue.Version;

        // Someone else's edit lands first and moves the token on.
        var first = await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "Edited by the other writer", priority = "Medium" });
        first.EnsureSuccessStatusCode();

        // Our edit was built on the version we read before that, so it is refused
        // rather than silently clobbering the change we never saw.
        var second = await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "Edited by us", priority = "Low", version = staleVersion });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Update_with_the_current_version_succeeds()
    {
        var issue = await CreateIssueAsync("Fresh update subject");

        var response = await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "Edited with the right version", priority = "High", version = issue.Version });

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Writes_are_rate_limited_while_reads_are_not()
    {
        await using var factory = new TracerApiFactory
        {
            RateLimitPermitLimit = 3,
            RateLimitWindowSeconds = 300,
        };
        var client = factory.CreateAdminClient();

        // Reads never draw from the bucket, however many there are.
        for (var i = 0; i < 8; i++)
        {
            var read = await client.GetAsync("/api/teams");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        // Writes do: the fourth POST in the window is refused.
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? throttled = null;
        for (var i = 0; i < 5; i++)
        {
            var write = await client.PostAsJsonAsync("/api/teams", new { name = $"RL {i}", key = $"RL{i}" });
            statuses.Add(write.StatusCode);
            if (write.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throttled = write;
            }
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.NotNull(throttled);
        Assert.Equal("application/problem+json", throttled!.Content.Headers.ContentType?.MediaType);
    }

    private async Task<TeamPayload> EngTeamAsync()
    {
        var teams = await _client.GetListAsync<TeamPayload>("/api/teams");
        return teams.Single(t => t.Key == "ENG");
    }

    private async Task<IssuePayload> CreateIssueAsync(string title)
    {
        var eng = await EngTeamAsync();
        var response = await _client.PostAsJsonAsync($"/api/teams/{eng.Id}/issues", new { title });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);

    private sealed record ProjectPayload(Guid Id, string Name);

    private sealed record IssuePayload(Guid Id, string Title, Guid Version);
}
