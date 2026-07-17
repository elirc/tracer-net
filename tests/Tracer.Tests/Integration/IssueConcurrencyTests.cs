using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tracer.Infrastructure;

namespace Tracer.Tests.Integration;

/// <summary>
/// The optimistic-concurrency token on an issue: a stale write loses rather than
/// silently clobbering the change it never saw.
/// </summary>
public class IssueConcurrencyTests : IClassFixture<TracerApiFactory>
{
    private readonly TracerApiFactory _factory;
    private readonly HttpClient _client;

    public IssueConcurrencyTests(TracerApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAdminClient();
    }

    [Fact]
    public async Task Two_writers_on_one_issue_the_stale_save_throws_and_the_first_write_stands()
    {
        var issueId = await CreateIssueAsync("Contended issue");

        // Two contexts load the same row — the same version — before either writes.
        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<TracerDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<TracerDbContext>();

        var a = await dbA.Issues.SingleAsync(i => i.Id == issueId);
        var b = await dbB.Issues.SingleAsync(i => i.Id == issueId);

        // Writer A commits first, rotating the token exactly as the controller does.
        a.Title = "A got there first";
        a.Version = Guid.NewGuid();
        await dbA.SaveChangesAsync();

        // Writer B is now stale: its UPDATE's WHERE Version = <what B read> matches
        // no row, so EF raises the concurrency exception the controller turns into
        // a 409 rather than letting B's write win.
        b.Title = "B was too late";
        b.Version = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

        using var scopeC = _factory.Services.CreateScope();
        var dbC = scopeC.ServiceProvider.GetRequiredService<TracerDbContext>();
        var current = await dbC.Issues.AsNoTracking().SingleAsync(i => i.Id == issueId);
        Assert.Equal("A got there first", current.Title);
    }

    [Fact]
    public async Task A_transition_rotates_the_version_token()
    {
        var (issue, todoStateId) = await CreateIssueAndTodoStateAsync();

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions",
            new { stateId = todoStateId });
        response.EnsureSuccessStatusCode();
        var afterTransition = (await response.Content.ReadFromJsonAsync<IssuePayload>())!;

        Assert.NotEqual(issue.Version, afterTransition.Version);
    }

    [Fact]
    public async Task A_second_stale_update_over_http_is_rejected_and_leaves_the_first_intact()
    {
        var issue = await CreateIssuePayloadAsync("Stale http subject");
        var staleVersion = issue.Version;

        var first = await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "Winner", priority = "High", version = staleVersion });
        first.EnsureSuccessStatusCode();

        var second = await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "Loser", priority = "Low", version = staleVersion });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var current = await _client.GetFromJsonAsync<IssuePayload>($"/api/issues/{issue.Id}");
        Assert.Equal("Winner", current!.Title);
    }

    private async Task<TeamPayload> EngTeamAsync()
    {
        var teams = await _client.GetListAsync<TeamPayload>("/api/teams");
        return teams.Single(t => t.Key == "ENG");
    }

    private async Task<Guid> CreateIssueAsync(string title) => (await CreateIssuePayloadAsync(title)).Id;

    private async Task<IssuePayload> CreateIssuePayloadAsync(string title)
    {
        var eng = await EngTeamAsync();
        var response = await _client.PostAsJsonAsync($"/api/teams/{eng.Id}/issues", new { title });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    private async Task<(IssuePayload Issue, Guid TodoStateId)> CreateIssueAndTodoStateAsync()
    {
        var eng = await EngTeamAsync();
        var states = await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{eng.Id}/states");
        var todo = states!.Single(s => s.Name == "Todo");
        var issue = await CreateIssuePayloadAsync("Transition subject");
        return (issue, todo.Id);
    }

    private sealed record TeamPayload(Guid Id, string Key);

    private sealed record StatePayload(Guid Id, string Name);

    private sealed record IssuePayload(Guid Id, string Title, Guid Version);
}
