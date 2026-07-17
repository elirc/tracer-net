using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// The workflow state machine at the HTTP boundary: the whole forbidden set is a
/// 422 <c>problem+json</c> with the exact title and a detail that names both
/// states, and the reopen paths that look similar are allowed. The unit tests
/// prove the category rules; this proves the API reports them as designed.
/// </summary>
public class InvalidTransitionMatrixTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public InvalidTransitionMatrixTests(TracerApiFactory factory)
    {
        _client = factory.CreateAdminClient();
    }

    [Theory]
    [InlineData("Backlog", "Done")]     // skipping straight to done
    [InlineData("Todo", "Done")]        // same, from todo
    [InlineData("Done", "Canceled")]    // cancelling finished work
    [InlineData("Canceled", "In Progress")] // resurrecting straight into progress
    [InlineData("Canceled", "Done")]        // resurrecting straight into done
    public async Task A_forbidden_transition_is_422_problem_json_naming_both_states(string from, string to)
    {
        var (issue, states) = await IssueInStateAsync(from);
        var target = states.Single(s => s.Name == to);

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions",
            new { stateId = target.Id });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("Invalid state transition.", problem!.Title);
        Assert.Equal(422, problem.Status);
        Assert.Contains(from, problem.Detail);
        Assert.Contains(to, problem.Detail);
    }

    [Theory]
    [InlineData("Backlog", "In Progress")]
    [InlineData("Backlog", "Canceled")]
    [InlineData("Todo", "In Progress")]
    [InlineData("In Progress", "Done")]
    [InlineData("Done", "Todo")]        // reopen
    [InlineData("Done", "In Progress")] // reopen
    [InlineData("Canceled", "Backlog")] // reopen
    [InlineData("Canceled", "Todo")]    // reopen
    public async Task An_allowed_transition_succeeds(string from, string to)
    {
        var (issue, states) = await IssueInStateAsync(from);
        var target = states.Single(s => s.Name == to);

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions",
            new { stateId = target.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Creates an issue and walks it into <paramref name="stateName"/> by a legal
    /// path, so the transition under test starts from exactly the state it claims.
    /// </summary>
    private async Task<(IssuePayload Issue, List<StatePayload> States)> IssueInStateAsync(string stateName)
    {
        // A dedicated team per case, so states and issues never collide across the
        // parameterized runs.
        var key = $"MX{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var team = await CreateTeamAsync(key);
        var states = await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{team.Id}/states");

        var created = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/issues", new { title = $"MX {stateName}" });
        created.EnsureSuccessStatusCode();
        var issue = (await created.Content.ReadFromJsonAsync<IssuePayload>())!;

        // Legal routes from the default Backlog start to every category.
        var path = stateName switch
        {
            "Backlog" => Array.Empty<string>(),
            "Todo" => ["Todo"],
            "In Progress" => ["In Progress"],
            "Done" => ["In Progress", "Done"],
            "Canceled" => ["Canceled"],
            _ => throw new ArgumentOutOfRangeException(nameof(stateName), stateName, null),
        };

        foreach (var step in path)
        {
            var target = states!.Single(s => s.Name == step);
            var move = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions", new { stateId = target.Id });
            move.EnsureSuccessStatusCode();
        }

        return (issue, states!);
    }

    private async Task<TeamPayload> CreateTeamAsync(string key)
    {
        var response = await _client.PostAsJsonAsync("/api/teams", new { name = $"Matrix {key}", key });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TeamPayload>())!;
    }

    private sealed record TeamPayload(Guid Id, string Key);

    private sealed record StatePayload(Guid Id, string Name);

    private sealed record IssuePayload(Guid Id, string Title);

    private sealed record ProblemPayload(string Title, int Status, string Detail);
}
