using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

public class IssueOrderingApiTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public IssueOrderingApiTests(TracerApiFactory factory)
    {
        _client = factory.CreateAdminClient();
    }

    private sealed record TeamPayload(Guid Id, string Key);
    private sealed record StatePayload(Guid Id, string Name, string Type, int Position);
    private sealed record IssuePayload(Guid Id, string Identifier, string Title, Guid StateId, string State, double Position);

    private async Task<(TeamPayload Team, List<StatePayload> States)> CreateTeamWithStatesAsync(string name, string key)
    {
        var response = await _client.PostAsJsonAsync("/api/teams", new { name, key });
        response.EnsureSuccessStatusCode();
        var team = (await response.Content.ReadFromJsonAsync<TeamPayload>())!;
        var states = (await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{team.Id}/states"))!;
        return (team, states);
    }

    private async Task<IssuePayload> CreateIssueAsync(Guid teamId, string title, Guid? stateId = null)
    {
        var response = await _client.PostAsJsonAsync($"/api/teams/{teamId}/issues", new { title, stateId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    private async Task<List<string>> ColumnOrderAsync(Guid teamId, Guid stateId)
    {
        var issues = await _client.GetFromJsonAsync<List<IssuePayload>>($"/api/teams/{teamId}/issues");
        return issues!
            .Where(i => i.StateId == stateId)
            .OrderBy(i => i.Position)
            .Select(i => i.Title)
            .ToList();
    }

    [Fact]
    public async Task Reorder_moves_an_issue_between_two_neighbours()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Order A", "ORA");
        var backlog = states.Single(s => s.Name == "Backlog");
        var a = await CreateIssueAsync(team.Id, "a");
        var b = await CreateIssueAsync(team.Id, "b");
        var c = await CreateIssueAsync(team.Id, "c");

        // Drag c up between a and b.
        var response = await _client.PostAsJsonAsync($"/api/issues/{c.Id}/reorder",
            new { afterIssueId = a.Id, beforeIssueId = b.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var moved = await response.Content.ReadFromJsonAsync<IssuePayload>();
        Assert.True(moved!.Position > a.Position);
        Assert.True(moved.Position < b.Position);
        Assert.Equal(["a", "c", "b"], await ColumnOrderAsync(team.Id, backlog.Id));
    }

    [Fact]
    public async Task Reorder_only_rewrites_the_moved_issue()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Order B", "ORB");
        var backlog = states.Single(s => s.Name == "Backlog");
        var a = await CreateIssueAsync(team.Id, "a");
        var b = await CreateIssueAsync(team.Id, "b");
        var c = await CreateIssueAsync(team.Id, "c");

        await _client.PostAsJsonAsync($"/api/issues/{c.Id}/reorder", new { afterIssueId = a.Id, beforeIssueId = b.Id });

        // The neighbours keep the ranks they were created with: a reorder is a
        // single-row update, not a renumbering of the column.
        var issues = (await _client.GetFromJsonAsync<List<IssuePayload>>($"/api/teams/{team.Id}/issues"))!
            .Where(i => i.StateId == backlog.Id)
            .ToDictionary(i => i.Title, i => i.Position);
        Assert.Equal(a.Position, issues["a"]);
        Assert.Equal(b.Position, issues["b"]);
    }

    [Fact]
    public async Task Reorder_to_the_top_of_a_column()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Order C", "ORC");
        var backlog = states.Single(s => s.Name == "Backlog");
        var a = await CreateIssueAsync(team.Id, "a");
        await CreateIssueAsync(team.Id, "b");
        var c = await CreateIssueAsync(team.Id, "c");

        await _client.PostAsJsonAsync($"/api/issues/{c.Id}/reorder", new { beforeIssueId = a.Id });

        Assert.Equal(["c", "a", "b"], await ColumnOrderAsync(team.Id, backlog.Id));
    }

    [Fact]
    public async Task Reorder_with_no_neighbours_appends_to_the_end()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Order D", "ORD");
        var backlog = states.Single(s => s.Name == "Backlog");
        var a = await CreateIssueAsync(team.Id, "a");
        await CreateIssueAsync(team.Id, "b");
        await CreateIssueAsync(team.Id, "c");

        await _client.PostAsJsonAsync($"/api/issues/{a.Id}/reorder", new { });

        Assert.Equal(["b", "c", "a"], await ColumnOrderAsync(team.Id, backlog.Id));
    }

    [Fact]
    public async Task Reorder_after_an_issue_lands_directly_behind_it()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Order E", "ORE");
        var backlog = states.Single(s => s.Name == "Backlog");
        await CreateIssueAsync(team.Id, "a");
        var b = await CreateIssueAsync(team.Id, "b");
        await CreateIssueAsync(team.Id, "c");
        var d = await CreateIssueAsync(team.Id, "d");

        await _client.PostAsJsonAsync($"/api/issues/{d.Id}/reorder", new { afterIssueId = b.Id });

        Assert.Equal(["a", "b", "d", "c"], await ColumnOrderAsync(team.Id, backlog.Id));
    }

    [Fact]
    public async Task Reorder_into_another_column_moves_and_ranks_the_issue()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Order F", "ORF");
        var todo = states.Single(s => s.Name == "Todo");
        var existing = await CreateIssueAsync(team.Id, "already todo", todo.Id);
        var issue = await CreateIssueAsync(team.Id, "from backlog");

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/reorder",
            new { stateId = todo.Id, beforeIssueId = existing.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var moved = await response.Content.ReadFromJsonAsync<IssuePayload>();
        Assert.Equal("Todo", moved!.State);
        Assert.True(moved.Position < existing.Position);
        Assert.Equal(["from backlog", "already todo"], await ColumnOrderAsync(team.Id, todo.Id));
    }

    [Fact]
    public async Task Reorder_cannot_smuggle_an_issue_through_an_invalid_transition()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Order G", "ORG");
        var done = states.Single(s => s.Name == "Done");
        var issue = await CreateIssueAsync(team.Id, "still in the backlog");

        // Backlog -> Done is forbidden by the state machine; dragging the card
        // must be refused exactly like an explicit transition would be.
        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/reorder", new { stateId = done.Id });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Invalid state transition", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reorder_into_another_teams_state_returns_400()
    {
        var (teamA, _) = await CreateTeamWithStatesAsync("Order H", "ORH");
        var (_, statesB) = await CreateTeamWithStatesAsync("Order I", "ORI");
        var issue = await CreateIssueAsync(teamA.Id, "team-bound");

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/reorder",
            new { stateId = statesB.Single(s => s.Name == "Todo").Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reorder_against_an_issue_outside_the_target_column_returns_400()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Order J", "ORJ");
        var todo = states.Single(s => s.Name == "Todo");
        var elsewhere = await CreateIssueAsync(team.Id, "in todo", todo.Id);
        var issue = await CreateIssueAsync(team.Id, "in backlog");

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/reorder",
            new { afterIssueId = elsewhere.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reorder_with_neighbours_in_the_wrong_order_returns_400()
    {
        var (team, _) = await CreateTeamWithStatesAsync("Order K", "ORK");
        var a = await CreateIssueAsync(team.Id, "a");
        var b = await CreateIssueAsync(team.Id, "b");
        var c = await CreateIssueAsync(team.Id, "c");

        var response = await _client.PostAsJsonAsync($"/api/issues/{c.Id}/reorder",
            new { afterIssueId = b.Id, beforeIssueId = a.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("must come before", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reorder_against_itself_returns_400()
    {
        var (team, _) = await CreateTeamWithStatesAsync("Order L", "ORL");
        var issue = await CreateIssueAsync(team.Id, "lonely");

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/reorder",
            new { afterIssueId = issue.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reorder_of_an_unknown_issue_returns_404()
    {
        var response = await _client.PostAsJsonAsync($"/api/issues/{Guid.NewGuid()}/reorder", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Column_rebalances_once_a_gap_is_split_too_many_times()
    {
        var (team, states) = await CreateTeamWithStatesAsync("Order M", "ORM");
        var backlog = states.Single(s => s.Name == "Backlog");
        var a = await CreateIssueAsync(team.Id, "a");
        var b = await CreateIssueAsync(team.Id, "b");
        Assert.Equal(2.0, b.Position);

        // Wedge issue after issue into the gap directly below "a". Each one
        // halves the remaining gap, so after ~20 drops there is no midpoint
        // left and the column has to be renumbered.
        var closest = b;
        const int drops = 40;
        for (var i = 0; i < drops; i++)
        {
            var wedge = await CreateIssueAsync(team.Id, $"x{i}");
            var response = await _client.PostAsJsonAsync($"/api/issues/{wedge.Id}/reorder",
                new { afterIssueId = a.Id, beforeIssueId = closest.Id });
            response.EnsureSuccessStatusCode();
            closest = (await response.Content.ReadFromJsonAsync<IssuePayload>())!;
        }

        var column = (await _client.GetFromJsonAsync<List<IssuePayload>>($"/api/teams/{team.Id}/issues"))!
            .Where(i => i.StateId == backlog.Id)
            .OrderBy(i => i.Position)
            .ToList();

        // A rebalance happened: "b" could never have drifted off its original
        // rank of 2.0 by a single-row midpoint update alone.
        var finalB = column.Single(i => i.Title == "b");
        Assert.True(finalB.Position > 2.0, $"expected the column to have been renumbered, b sits at {finalB.Position}");

        // The renumbering preserved order and left every rank distinct.
        Assert.Equal(drops + 2, column.Count);
        Assert.Equal("a", column.First().Title);
        Assert.Equal("b", column.Last().Title);
        Assert.Equal(column.Count, column.Select(i => i.Position).Distinct().Count());
        // Each wedge landed below "a" and above the one before it.
        Assert.Equal(
            Enumerable.Range(0, drops).Reverse().Select(i => $"x{i}"),
            column.Skip(1).SkipLast(1).Select(i => i.Title));
    }
}
