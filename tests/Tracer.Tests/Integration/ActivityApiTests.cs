using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// The audit trail. These tests are the only thing standing between the product
/// and a silently incomplete feed: recording is an explicit call in each
/// controller, so "someone added a mutation and forgot to log it" is caught
/// here or not at all.
/// </summary>
public class ActivityApiTests : IClassFixture<TracerApiFactory>
{
    private readonly TracerApiFactory _factory;
    private readonly HttpClient _client;

    public ActivityApiTests(TracerApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAdminClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record IssuePayload(Guid Id, Guid TeamId, string Identifier, string Title, Guid StateId);
    private sealed record StatePayload(Guid Id, string Name, string Type, int Position);
    private sealed record LabelPayload(Guid Id, string Name);
    private sealed record CommentPayload(Guid Id, string Author, string Body);
    private sealed record RelationPayload(Guid Id, string Kind, Guid IssueId);
    private sealed record ActivityPayload(
        Guid Id,
        Guid TeamId,
        Guid IssueId,
        string Identifier,
        string IssueTitle,
        string Type,
        string? Field,
        string? OldValue,
        string? NewValue,
        Guid? ActorId,
        string Actor,
        DateTimeOffset CreatedAt);
    private sealed record PagedActivity(List<ActivityPayload> Items, int Page, int PageSize, int Total, int TotalPages);

    private async Task<TeamPayload> CreateTeamAsync(string name, string key)
    {
        var created = await _client.PostAsJsonAsync("/api/teams", new { name, key });
        return (await created.Content.ReadFromJsonAsync<TeamPayload>())!;
    }

    private async Task<IssuePayload> CreateIssueAsync(Guid teamId, string title, HttpClient? client = null)
    {
        var created = await (client ?? _client).PostAsJsonAsync($"/api/teams/{teamId}/issues", new { title });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    private async Task<List<StatePayload>> StatesAsync(Guid teamId) =>
        (await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{teamId}/states"))!;

    private async Task<List<ActivityPayload>> IssueFeedAsync(Guid issueId) =>
        (await _client.GetFromJsonAsync<PagedActivity>($"/api/issues/{issueId}/activity"))!.Items;

    private async Task<PagedActivity> TeamFeedAsync(Guid teamId, string query = "") =>
        (await _client.GetFromJsonAsync<PagedActivity>($"/api/teams/{teamId}/activity{query}"))!;

    // ---- Every mutation is recorded ----

    [Fact]
    public async Task Creating_an_issue_is_recorded_with_its_actor()
    {
        var team = await CreateTeamAsync("Act A", "ACA");
        var issue = await CreateIssueAsync(team.Id, "born");

        var entry = Assert.Single(await IssueFeedAsync(issue.Id));
        Assert.Equal("IssueCreated", entry.Type);
        Assert.Equal("ana", entry.Actor);
        Assert.Equal(issue.Id, entry.IssueId);
        Assert.Equal(issue.Identifier, entry.Identifier);
        Assert.Equal("born", entry.NewValue);
    }

    [Fact]
    public async Task A_state_change_records_both_sides_of_the_move()
    {
        var team = await CreateTeamAsync("Act B", "ACB");
        var issue = await CreateIssueAsync(team.Id, "moving");
        var todo = (await StatesAsync(team.Id)).Single(s => s.Type == "Todo");

        await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions", new { stateId = todo.Id });

        var entry = (await IssueFeedAsync(issue.Id)).First();
        Assert.Equal("IssueStateChanged", entry.Type);
        Assert.Equal("Backlog", entry.OldValue);
        Assert.Equal("Todo", entry.NewValue);
    }

    /// <summary>
    /// Dragging a card across columns is the same event as an explicit
    /// transition, because to anyone reading the feed it is the same thing.
    /// </summary>
    [Fact]
    public async Task Reordering_into_another_column_records_a_state_change()
    {
        var team = await CreateTeamAsync("Act C", "ACC");
        var issue = await CreateIssueAsync(team.Id, "dragged");
        var todo = (await StatesAsync(team.Id)).Single(s => s.Type == "Todo");

        await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/reorder", new { stateId = todo.Id });

        var entry = (await IssueFeedAsync(issue.Id)).First();
        Assert.Equal("IssueStateChanged", entry.Type);
        Assert.Equal("Todo", entry.NewValue);
    }

    /// <summary>
    /// Rank is a view preference. Logging it would bury a feed under the noise of
    /// one person tidying their board.
    /// </summary>
    [Fact]
    public async Task Reordering_within_a_column_records_nothing()
    {
        var team = await CreateTeamAsync("Act D", "ACD");
        var first = await CreateIssueAsync(team.Id, "first");
        var second = await CreateIssueAsync(team.Id, "second");

        await _client.PostAsJsonAsync($"/api/issues/{second.Id}/reorder", new { beforeIssueId = first.Id });

        var feed = await IssueFeedAsync(second.Id);
        Assert.Equal(["IssueCreated"], feed.Select(a => a.Type).ToArray());
    }

    [Fact]
    public async Task Assignment_and_unassignment_are_their_own_event()
    {
        var team = await CreateTeamAsync("Act E", "ACE");
        var issue = await CreateIssueAsync(team.Id, "work");

        await _client.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "work", assignee = "ben" });
        var assigned = (await IssueFeedAsync(issue.Id)).First();
        Assert.Equal("IssueAssigned", assigned.Type);
        Assert.Null(assigned.OldValue);
        Assert.Equal("ben", assigned.NewValue);

        await _client.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "work" });
        var unassigned = (await IssueFeedAsync(issue.Id)).First();
        Assert.Equal("IssueAssigned", unassigned.Type);
        Assert.Equal("ben", unassigned.OldValue);
        Assert.Null(unassigned.NewValue);
    }

    [Fact]
    public async Task Each_edited_field_gets_its_own_entry()
    {
        var team = await CreateTeamAsync("Act F", "ACF");
        var issue = await CreateIssueAsync(team.Id, "before");

        await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "after", priority = "High", estimate = 5 });

        var edits = (await IssueFeedAsync(issue.Id)).Where(a => a.Type == "IssueUpdated").ToList();
        Assert.Equal(3, edits.Count);

        var title = edits.Single(a => a.Field == "title");
        Assert.Equal("before", title.OldValue);
        Assert.Equal("after", title.NewValue);

        var priority = edits.Single(a => a.Field == "priority");
        Assert.Equal("None", priority.OldValue);
        Assert.Equal("High", priority.NewValue);

        Assert.Single(edits, a => a.Field == "estimate");
    }

    /// <summary>
    /// A form re-sends every field on every save. If that wrote history, one edit
    /// would produce a wall of "ana updated this" and the feed would stop being
    /// read.
    /// </summary>
    [Fact]
    public async Task Re_saving_an_issue_unchanged_records_nothing()
    {
        var team = await CreateTeamAsync("Act G", "ACG");
        var issue = await CreateIssueAsync(team.Id, "steady");
        await _client.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "steady", priority = "Medium" });
        var before = (await IssueFeedAsync(issue.Id)).Count;

        // Byte-for-byte the same PUT again.
        await _client.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "steady", priority = "Medium" });

        Assert.Equal(before, (await IssueFeedAsync(issue.Id)).Count);
    }

    [Fact]
    public async Task A_description_change_is_recorded_without_copying_the_body()
    {
        var team = await CreateTeamAsync("Act H", "ACH");
        var issue = await CreateIssueAsync(team.Id, "documented");

        await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "documented", description = new string('x', 4000) });

        var entry = (await IssueFeedAsync(issue.Id)).First(a => a.Field == "description");
        Assert.Equal("IssueUpdated", entry.Type);
        // The fact of the change is the useful part; the log is not a second
        // copy of the descriptions table.
        Assert.Null(entry.OldValue);
        Assert.Null(entry.NewValue);
    }

    [Fact]
    public async Task Labels_record_both_attaching_and_detaching()
    {
        var team = await CreateTeamAsync("Act I", "ACI");
        var issue = await CreateIssueAsync(team.Id, "tagged");
        var label = await (await _client.PostAsJsonAsync($"/api/teams/{team.Id}/labels", new { name = "urgent" }))
            .Content.ReadFromJsonAsync<LabelPayload>();

        await _client.PutAsync($"/api/issues/{issue.Id}/labels/{label!.Id}", null);
        var added = (await IssueFeedAsync(issue.Id)).First();
        Assert.Equal("IssueLabelAdded", added.Type);
        Assert.Equal("urgent", added.NewValue);

        await _client.DeleteAsync($"/api/issues/{issue.Id}/labels/{label.Id}");
        var removed = (await IssueFeedAsync(issue.Id)).First();
        Assert.Equal("IssueLabelRemoved", removed.Type);
        Assert.Equal("urgent", removed.OldValue);
    }

    /// <summary>Attaching a label twice is a no-op, and a no-op must not invent history.</summary>
    [Fact]
    public async Task Re_attaching_a_label_records_nothing()
    {
        var team = await CreateTeamAsync("Act J", "ACJ");
        var issue = await CreateIssueAsync(team.Id, "tagged twice");
        var label = await (await _client.PostAsJsonAsync($"/api/teams/{team.Id}/labels", new { name = "dupe" }))
            .Content.ReadFromJsonAsync<LabelPayload>();

        await _client.PutAsync($"/api/issues/{issue.Id}/labels/{label!.Id}", null);
        var before = (await IssueFeedAsync(issue.Id)).Count;
        await _client.PutAsync($"/api/issues/{issue.Id}/labels/{label.Id}", null);

        Assert.Equal(before, (await IssueFeedAsync(issue.Id)).Count);
    }

    [Fact]
    public async Task Comments_record_creation_edit_and_deletion()
    {
        var team = await CreateTeamAsync("Act K", "ACK");
        var issue = await CreateIssueAsync(team.Id, "discussed");

        var comment = await (await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/comments",
            new { body = "hello" })).Content.ReadFromJsonAsync<CommentPayload>();
        Assert.Equal("CommentCreated", (await IssueFeedAsync(issue.Id)).First().Type);

        await _client.PutAsJsonAsync($"/api/comments/{comment!.Id}", new { body = "edited" });
        Assert.Equal("CommentUpdated", (await IssueFeedAsync(issue.Id)).First().Type);

        await _client.DeleteAsync($"/api/comments/{comment.Id}");
        var deleted = (await IssueFeedAsync(issue.Id)).First();
        Assert.Equal("CommentDeleted", deleted.Type);
        Assert.Equal("edited", deleted.OldValue);
    }

    [Fact]
    public async Task A_long_comment_is_excerpted_not_copied_whole()
    {
        var team = await CreateTeamAsync("Act L", "ACL");
        var issue = await CreateIssueAsync(team.Id, "verbose");

        await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { body = new string('y', 5000) });

        var entry = (await IssueFeedAsync(issue.Id)).First();
        Assert.NotNull(entry.NewValue);
        Assert.True(entry.NewValue.Length < 200, $"excerpt was {entry.NewValue.Length} chars");
        Assert.EndsWith("…", entry.NewValue);
    }

    [Fact]
    public async Task Relations_record_adding_and_removing_in_the_callers_words()
    {
        var team = await CreateTeamAsync("Act M", "ACM");
        var blocked = await CreateIssueAsync(team.Id, "waits");
        var blocker = await CreateIssueAsync(team.Id, "goes first");

        await _client.PostAsJsonAsync($"/api/issues/{blocked.Id}/relations",
            new { kind = "BlockedBy", issueId = blocker.Id });

        var added = (await IssueFeedAsync(blocked.Id)).First();
        Assert.Equal("IssueRelationAdded", added.Type);
        // Phrased as the caller phrased it, even though the stored row inverts.
        Assert.Equal("BlockedBy", added.Field);
        Assert.Equal(blocker.Identifier, added.NewValue);

        var relation = (await _client.GetFromJsonAsync<List<RelationPayload>>(
            $"/api/issues/{blocked.Id}/relations"))!.Single();
        await _client.DeleteAsync($"/api/issues/{blocked.Id}/relations/{relation.Id}");

        var removed = (await IssueFeedAsync(blocked.Id)).First();
        Assert.Equal("IssueRelationRemoved", removed.Type);
        Assert.Equal(blocker.Identifier, removed.OldValue);
    }

    [Fact]
    public async Task Re_parenting_is_recorded_with_both_identifiers()
    {
        var team = await CreateTeamAsync("Act N", "ACN");
        var parent = await CreateIssueAsync(team.Id, "epic");
        var child = await CreateIssueAsync(team.Id, "task");

        await _client.PutAsJsonAsync($"/api/issues/{child.Id}", new { title = "task", parentId = parent.Id });
        var nested = (await IssueFeedAsync(child.Id)).First();
        Assert.Equal("IssueParentChanged", nested.Type);
        Assert.Null(nested.OldValue);
        Assert.Equal(parent.Identifier, nested.NewValue);

        await _client.PutAsJsonAsync($"/api/issues/{child.Id}", new { title = "task" });
        var unnested = (await IssueFeedAsync(child.Id)).First();
        Assert.Equal(parent.Identifier, unnested.OldValue);
        Assert.Null(unnested.NewValue);
    }

    // ---- Surviving deletion: the point of the whole thing ----

    /// <summary>
    /// The reason <c>Activity.IssueId</c> carries no foreign key. A cascade here
    /// would erase the record of the deletion along with the issue — losing the
    /// one question an audit log exists to answer.
    /// </summary>
    [Fact]
    public async Task A_deleted_issues_history_survives_in_the_team_feed()
    {
        var team = await CreateTeamAsync("Act O", "ACO");
        var issue = await CreateIssueAsync(team.Id, "doomed");
        await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { body = "last words" });

        await _client.DeleteAsync($"/api/issues/{issue.Id}");

        // The issue is gone...
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/issues/{issue.Id}")).StatusCode);

        // ...but its history, and who ended it, is not.
        var feed = await TeamFeedAsync(team.Id, $"?issueId={issue.Id}");
        Assert.Contains(feed.Items, a => a.Type == "IssueDeleted" && a.Actor == "ana");
        Assert.Contains(feed.Items, a => a.Type == "CommentCreated");
        Assert.Contains(feed.Items, a => a.Type == "IssueCreated");
    }

    /// <summary>The title is denormalized so a deleted issue still reads as something.</summary>
    [Fact]
    public async Task A_deleted_issues_entries_still_name_it()
    {
        var team = await CreateTeamAsync("Act P", "ACP");
        var issue = await CreateIssueAsync(team.Id, "remember me");

        await _client.DeleteAsync($"/api/issues/{issue.Id}");

        var feed = await TeamFeedAsync(team.Id, $"?issueId={issue.Id}");
        var entry = feed.Items.First(a => a.Type == "IssueDeleted");
        Assert.Equal("remember me", entry.IssueTitle);
        Assert.Equal(issue.Identifier, entry.Identifier);
    }

    // ---- Immutability ----

    [Fact]
    public async Task The_feed_offers_no_way_to_rewrite_itself()
    {
        var team = await CreateTeamAsync("Act Q", "ACQ");
        var issue = await CreateIssueAsync(team.Id, "permanent");
        var entry = (await IssueFeedAsync(issue.Id)).Single();

        // No write routes exist at all, so these do not resolve to a handler.
        foreach (var route in new[] { $"/api/activity/{entry.Id}", $"/api/teams/{team.Id}/activity/{entry.Id}" })
        {
            var deleted = await _client.DeleteAsync(route);
            Assert.True(
                deleted.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"DELETE {route} answered {deleted.StatusCode}");
        }

        var posted = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/activity", new { type = "IssueCreated" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, posted.StatusCode);
    }

    // ---- Filters, ordering, paging ----

    [Fact]
    public async Task The_feed_is_newest_first()
    {
        var team = await CreateTeamAsync("Act R", "ACR");
        var issue = await CreateIssueAsync(team.Id, "ordered");
        var todo = (await StatesAsync(team.Id)).Single(s => s.Type == "Todo");
        await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions", new { stateId = todo.Id });

        var feed = await IssueFeedAsync(issue.Id);

        Assert.Equal(["IssueStateChanged", "IssueCreated"], feed.Select(a => a.Type).ToArray());
    }

    [Fact]
    public async Task The_team_feed_filters_by_type_actor_and_issue()
    {
        var team = await CreateTeamAsync("Act S", "ACS");
        var users = await _client.GetFromJsonAsync<List<UserRow>>("/api/users");
        var ben = users!.Single(u => u.Handle == "ben");
        await _client.PutAsync($"/api/users/{ben.Id}/teams/{team.Id}", null);

        var mine = await CreateIssueAsync(team.Id, "by ana");
        var theirs = await CreateIssueAsync(team.Id, "by ben", _factory.CreateEngMemberClient());

        var byType = await TeamFeedAsync(team.Id, "?type=IssueCreated");
        Assert.Equal(2, byType.Total);

        var byActor = await TeamFeedAsync(team.Id, "?actor=ben");
        Assert.Equal(theirs.Id, Assert.Single(byActor.Items).IssueId);

        // Case-insensitive, as the issue search's assignee filter already is.
        Assert.Equal(1, (await TeamFeedAsync(team.Id, "?actor=BEN")).Total);

        var byIssue = await TeamFeedAsync(team.Id, $"?issueId={mine.Id}");
        Assert.Equal(mine.Id, Assert.Single(byIssue.Items).IssueId);
    }

    private sealed record UserRow(Guid Id, string Handle);

    [Fact]
    public async Task The_team_feed_filters_by_a_half_open_time_window()
    {
        var team = await CreateTeamAsync("Act T", "ACT");
        var issue = await CreateIssueAsync(team.Id, "timed");
        var entry = (await IssueFeedAsync(issue.Id)).Single();

        // [since, until) — the instant an entry was written is inside the window
        // that starts on it, and outside the one that ends on it.
        var inclusive = await TeamFeedAsync(team.Id, $"?since={Uri.EscapeDataString(entry.CreatedAt.ToString("o"))}");
        Assert.Contains(inclusive.Items, a => a.Id == entry.Id);

        var exclusive = await TeamFeedAsync(team.Id, $"?until={Uri.EscapeDataString(entry.CreatedAt.ToString("o"))}");
        Assert.DoesNotContain(exclusive.Items, a => a.Id == entry.Id);
    }

    [Fact]
    public async Task The_team_feed_pages()
    {
        var team = await CreateTeamAsync("Act U", "ACU");
        for (var i = 0; i < 5; i++)
        {
            await CreateIssueAsync(team.Id, $"issue {i}");
        }

        var first = await TeamFeedAsync(team.Id, "?page=1&pageSize=2");
        Assert.Equal(5, first.Total);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(2, first.Items.Count);

        var second = await TeamFeedAsync(team.Id, "?page=2&pageSize=2");
        Assert.Empty(first.Items.Select(a => a.Id).Intersect(second.Items.Select(a => a.Id)));
    }

    /// <summary>
    /// One save is one transaction, so its entries are stamped with one instant
    /// rather than with the order the C# happened to run in. Editing a title and
    /// a priority together did not happen twice, microseconds apart.
    /// </summary>
    [Fact]
    public async Task Entries_from_a_single_save_share_one_timestamp()
    {
        var team = await CreateTeamAsync("Act V", "ACV");
        var issue = await CreateIssueAsync(team.Id, "before");

        await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "after", priority = "High", estimate = 8 });

        var edits = (await IssueFeedAsync(issue.Id)).Where(a => a.Type == "IssueUpdated").ToList();

        Assert.Equal(3, edits.Count);
        Assert.Single(edits.Select(a => a.CreatedAt).Distinct());
    }

    /// <summary>
    /// And because they do collide, the id tiebreak is load-bearing rather than
    /// decorative: ordering by timestamp alone would leave these three in
    /// whatever order the planner felt like, and paging an unstable order
    /// silently repeats and skips rows.
    /// </summary>
    [Fact]
    public async Task Entries_sharing_a_timestamp_page_without_repeating_or_skipping()
    {
        var team = await CreateTeamAsync("Act W", "ACW");
        var issue = await CreateIssueAsync(team.Id, "before");

        // One PUT, three entries, one transaction, one timestamp.
        await _client.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "after", priority = "High", estimate = 8 });

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            seen.AddRange((await TeamFeedAsync(team.Id, $"?page={page}&pageSize=1")).Items.Select(a => a.Id));
        }

        Assert.Equal(4, seen.Count);
        Assert.Equal(4, seen.Distinct().Count());
    }

    // ---- Authorization ----

    [Fact]
    public async Task Another_teams_feed_and_timeline_are_out_of_reach()
    {
        var teams = await _client.GetFromJsonAsync<List<TeamPayload>>("/api/teams");
        var eng = teams!.Single(t => t.Key == "ENG");
        var issue = await CreateIssueAsync(eng.Id, "engineering business");
        var foreigner = _factory.CreateDesMemberClient();

        Assert.Equal(HttpStatusCode.NotFound, (await foreigner.GetAsync($"/api/teams/{eng.Id}/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreigner.GetAsync($"/api/issues/{issue.Id}/activity")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_issues_timeline_is_404()
    {
        var response = await _client.GetAsync($"/api/issues/{Guid.NewGuid()}/activity");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
