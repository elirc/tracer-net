using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// Subscriptions, auto-subscribe, fan-out, and the inbox.
///
/// The seeded workspace gives three real accounts on Engineering — <c>ana</c>
/// (admin), <c>ben</c>, and (once added) <c>dana</c> — so "who got pinged" is a
/// question with real people on both sides of it, which is the only way to test a
/// fan-out that turns on who did what to whom.
/// </summary>
public class NotificationsApiTests : IClassFixture<TracerApiFactory>
{
    private readonly TracerApiFactory _factory;
    private readonly HttpClient _ana;   // admin, on every team
    private readonly HttpClient _ben;   // member of ENG

    public NotificationsApiTests(TracerApiFactory factory)
    {
        _factory = factory;
        _ana = factory.CreateAdminClient();
        _ben = factory.CreateEngMemberClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record IssuePayload(Guid Id, Guid TeamId, string Identifier, string Title, Guid StateId);
    private sealed record StatePayload(Guid Id, string Name, string Type);
    private sealed record UserRow(Guid Id, string Handle);
    private sealed record ActivityInner(string Type, string Identifier, string? OldValue, string? NewValue, string Actor);
    private sealed record NotificationPayload(Guid Id, bool IsRead, DateTimeOffset? ReadAt, DateTimeOffset CreatedAt, ActivityInner Activity);
    private sealed record PagedNotifications(List<NotificationPayload> Items, int Total);
    private sealed record UnreadCount(int Unread);
    private sealed record SubscriptionPayload(bool Subscribed, string? Reason);
    private sealed record SubscriberPayload(Guid UserId, string Handle, string Name, string Reason);

    // ---- Helpers: everything below acts on a fresh private team ----

    private async Task<TeamPayload> TeamAsync()
    {
        // A dedicated team per test so its members and its inbox counts are not
        // perturbed by other tests sharing the fixture.
        var key = $"N{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}";
        var created = await _ana.PostAsJsonAsync("/api/teams", new { name = $"Notif {key}", key });
        var team = (await created.Content.ReadFromJsonAsync<TeamPayload>())!;

        var users = (await _ana.GetFromJsonAsync<List<UserRow>>("/api/users"))!;
        await _ana.PutAsync($"/api/users/{users.Single(u => u.Handle == "ben").Id}/teams/{team.Id}", null);
        return team;
    }

    private async Task<IssuePayload> IssueAsync(HttpClient client, Guid teamId, string title, string? assignee = null)
    {
        var created = await client.PostAsJsonAsync($"/api/teams/{teamId}/issues", new { title, assignee });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    private async Task<List<NotificationPayload>> InboxAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<PagedNotifications>($"/api/notifications{query}"))!.Items;

    private async Task<int> UnreadAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<UnreadCount>("/api/notifications/unread-count"))!.Unread;

    /// <summary>
    /// Zeroes a user's badge before a test acts, so an absolute unread count
    /// means "what this test produced" rather than that plus whatever every other
    /// test sharing the fixture left in their inbox. Users are workspace-global —
    /// the per-test team isolates issues, not inboxes.
    /// </summary>
    private static async Task ZeroInboxAsync(HttpClient client) =>
        await client.PostAsync("/api/notifications/read-all", null);

    private async Task MoveToTodoAsync(Guid teamId, Guid issueId, HttpClient client)
    {
        var todo = (await _ana.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{teamId}/states"))!
            .Single(s => s.Type == "Todo");
        await client.PostAsJsonAsync($"/api/issues/{issueId}/transitions", new { stateId = todo.Id });
    }

    // ---- Auto-subscribe ----

    [Fact]
    public async Task Creating_an_issue_subscribes_its_author()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "mine now");

        var mine = await _ana.GetFromJsonAsync<SubscriptionPayload>($"/api/issues/{issue.Id}/subscription");
        Assert.True(mine!.Subscribed);
        Assert.Equal("Author", mine.Reason);
    }

    [Fact]
    public async Task Commenting_subscribes_the_commenter()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "discuss");

        await _ben.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { body = "chiming in" });

        var bens = await _ben.GetFromJsonAsync<SubscriptionPayload>($"/api/issues/{issue.Id}/subscription");
        Assert.True(bens!.Subscribed);
        Assert.Equal("Commenter", bens.Reason);
    }

    [Fact]
    public async Task Being_assigned_subscribes_the_assignee()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "for ben");

        await _ana.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "for ben", assignee = "ben" });

        var bens = await _ben.GetFromJsonAsync<SubscriptionPayload>($"/api/issues/{issue.Id}/subscription");
        Assert.True(bens!.Subscribed);
        Assert.Equal("Assignee", bens.Reason);
    }

    /// <summary>
    /// Assignees are free-form strings; a handle that owns no account cannot be
    /// routed an inbox item, and that is not an error.
    /// </summary>
    [Fact]
    public async Task Assigning_a_handle_with_no_account_subscribes_nobody()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "for a ghost");

        var response = await _ana.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "for a ghost", assignee = "nobody-by-that-name" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var subscribers = await _ana.GetFromJsonAsync<List<SubscriberPayload>>($"/api/issues/{issue.Id}/subscribers");
        Assert.DoesNotContain(subscribers!, s => s.Handle == "nobody-by-that-name");
    }

    // ---- Fan-out ----

    [Fact]
    public async Task A_comment_notifies_the_other_watchers_but_not_the_commenter()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "watched by ana");
        await ZeroInboxAsync(_ben);
        var anaBefore = await UnreadAsync(_ana);

        // ben comments: ana (author, a watcher) hears about it; ben does not ping
        // himself for his own comment.
        await _ben.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { body = "look at this" });

        Assert.Equal(anaBefore + 1, await UnreadAsync(_ana));
        Assert.Equal(0, await UnreadAsync(_ben));

        var top = (await InboxAsync(_ana)).First();
        Assert.Equal("CommentCreated", top.Activity.Type);
        Assert.Equal("ben", top.Activity.Actor);
    }

    [Fact]
    public async Task You_are_never_notified_of_your_own_action()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "solo");
        await ZeroInboxAsync(_ana);

        // ana does everything herself; nobody else watches.
        await MoveToTodoAsync(team.Id, issue.Id, _ana);
        await _ana.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { body = "note to self" });

        Assert.Equal(0, await UnreadAsync(_ana));
    }

    /// <summary>
    /// The most important notification the product sends: you are told you were
    /// assigned, in the same breath that puts you on the watch list.
    /// </summary>
    [Fact]
    public async Task Being_assigned_notifies_the_new_assignee()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "hand-off");
        await ZeroInboxAsync(_ben);

        await _ana.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "hand-off", assignee = "ben" });

        Assert.Equal(1, await UnreadAsync(_ben));
        var top = (await InboxAsync(_ben)).First();
        Assert.Equal("IssueAssigned", top.Activity.Type);
        Assert.Equal("ben", top.Activity.NewValue);
    }

    [Fact]
    public async Task A_state_change_notifies_the_watchers()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "moving");
        // ben watches by commenting.
        await _ben.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { body = "watching" });
        var benBefore = await UnreadAsync(_ben);

        await MoveToTodoAsync(team.Id, issue.Id, _ana);

        Assert.Equal(benBefore + 1, await UnreadAsync(_ben));
        Assert.Equal("IssueStateChanged", (await InboxAsync(_ben)).First().Activity.Type);
    }

    /// <summary>
    /// The audit log records a title edit; the inbox does not. Curation is the
    /// point — the information is in the feed, this is just refusing to page
    /// anyone about it.
    /// </summary>
    [Fact]
    public async Task An_unremarkable_edit_notifies_nobody()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "before");
        await _ben.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { body = "watching" });
        var benBefore = await UnreadAsync(_ben);

        await _ana.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "after", priority = "High" });

        Assert.Equal(benBefore, await UnreadAsync(_ben));
    }

    [Fact]
    public async Task A_manual_watcher_hears_about_a_state_change()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "ben opts in");
        // ben watches without commenting or being assigned.
        await _ben.PutAsync($"/api/issues/{issue.Id}/subscription", null);
        await ZeroInboxAsync(_ben);

        await MoveToTodoAsync(team.Id, issue.Id, _ana);

        Assert.Equal(1, await UnreadAsync(_ben));
    }

    // ---- Subscribing by hand ----

    [Fact]
    public async Task Subscribe_and_unsubscribe_round_trip()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "toggle");

        Assert.False((await _ben.GetFromJsonAsync<SubscriptionPayload>($"/api/issues/{issue.Id}/subscription"))!.Subscribed);

        var subscribed = await _ben.PutAsync($"/api/issues/{issue.Id}/subscription", null);
        Assert.Equal(HttpStatusCode.OK, subscribed.StatusCode);
        Assert.True((await _ben.GetFromJsonAsync<SubscriptionPayload>($"/api/issues/{issue.Id}/subscription"))!.Subscribed);

        Assert.Equal(HttpStatusCode.NoContent, (await _ben.DeleteAsync($"/api/issues/{issue.Id}/subscription")).StatusCode);
        Assert.False((await _ben.GetFromJsonAsync<SubscriptionPayload>($"/api/issues/{issue.Id}/subscription"))!.Subscribed);
    }

    [Fact]
    public async Task An_unsubscribed_watcher_stops_hearing_about_changes()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "quieted");
        await _ben.PutAsync($"/api/issues/{issue.Id}/subscription", null);
        await _ben.DeleteAsync($"/api/issues/{issue.Id}/subscription");
        var benBefore = await UnreadAsync(_ben);

        await MoveToTodoAsync(team.Id, issue.Id, _ana);

        Assert.Equal(benBefore, await UnreadAsync(_ben));
    }

    [Fact]
    public async Task Manual_subscribe_does_not_overwrite_an_existing_reason()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "already assigned");
        await _ana.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "already assigned", assignee = "ben" });

        // ben is on the list as the assignee; hitting "watch" keeps the truer reason.
        await _ben.PutAsync($"/api/issues/{issue.Id}/subscription", null);

        var mine = await _ben.GetFromJsonAsync<SubscriptionPayload>($"/api/issues/{issue.Id}/subscription");
        Assert.Equal("Assignee", mine!.Reason);
    }

    [Fact]
    public async Task The_subscriber_list_names_everyone_watching_and_why()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "crowd");
        await _ben.PutAsync($"/api/issues/{issue.Id}/subscription", null);

        var subscribers = await _ana.GetFromJsonAsync<List<SubscriberPayload>>($"/api/issues/{issue.Id}/subscribers");

        Assert.Contains(subscribers!, s => s.Handle == "ana" && s.Reason == "Author");
        Assert.Contains(subscribers!, s => s.Handle == "ben" && s.Reason == "Manual");
    }

    // ---- Read / unread ----

    [Fact]
    public async Task Marking_one_read_drops_the_unread_count()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "for reading");
        await ZeroInboxAsync(_ben);
        await _ana.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "for reading", assignee = "ben" });
        var notification = (await InboxAsync(_ben)).First();
        Assert.Equal(1, await UnreadAsync(_ben));

        var read = await _ben.PostAsync($"/api/notifications/{notification.Id}/read", null);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        Assert.Equal(0, await UnreadAsync(_ben));
        Assert.True((await InboxAsync(_ben)).First().IsRead);
    }

    [Fact]
    public async Task Marking_unread_brings_it_back()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "toggle read");
        await ZeroInboxAsync(_ben);
        await _ana.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "toggle read", assignee = "ben" });
        var notification = (await InboxAsync(_ben)).First();
        await _ben.PostAsync($"/api/notifications/{notification.Id}/read", null);

        await _ben.PostAsync($"/api/notifications/{notification.Id}/unread", null);

        Assert.Equal(1, await UnreadAsync(_ben));
    }

    [Fact]
    public async Task The_unread_filter_hides_read_notifications()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "filtering");
        await _ana.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "filtering", assignee = "ben" });
        var notification = (await InboxAsync(_ben)).First();
        await _ben.PostAsync($"/api/notifications/{notification.Id}/read", null);

        Assert.DoesNotContain(await InboxAsync(_ben, "?unread=true"), n => n.Id == notification.Id);
        Assert.Contains(await InboxAsync(_ben), n => n.Id == notification.Id); // still in the full inbox
    }

    [Fact]
    public async Task Mark_all_read_clears_the_badge()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "bulk");
        // Two notable things for ben to hear about.
        await _ana.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "bulk", assignee = "ben" });
        await MoveToTodoAsync(team.Id, issue.Id, _ana);
        Assert.True(await UnreadAsync(_ben) >= 2);

        var response = await _ben.PostAsync("/api/notifications/read-all", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(0, await UnreadAsync(_ben));
    }

    [Fact]
    public async Task Mark_all_read_on_an_empty_inbox_is_fine()
    {
        // ben has an inbox that may hold read items from other tests, but this
        // asserts the call succeeds and leaves zero unread regardless.
        var response = await _ben.PostAsync("/api/notifications/read-all", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await UnreadAsync(_ben));
    }

    // ---- The inbox is private ----

    [Fact]
    public async Task An_inbox_holds_only_its_owners_notifications()
    {
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "for ben only");
        await _ana.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "for ben only", assignee = "ben" });
        var bens = (await InboxAsync(_ben)).First();

        // ana cannot mark ben's notification read, and cannot see it as hers.
        Assert.Equal(HttpStatusCode.NotFound, (await _ana.PostAsync($"/api/notifications/{bens.Id}/read", null)).StatusCode);
        Assert.DoesNotContain(await InboxAsync(_ana), n => n.Id == bens.Id);
    }

    [Fact]
    public async Task Marking_an_unknown_notification_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _ana.PostAsync($"/api/notifications/{Guid.NewGuid()}/read", null)).StatusCode);
    }

    [Fact]
    public async Task Notifications_are_deduplicated_per_activity()
    {
        // One save, one notable activity, one notification — even though a save
        // can record several activities.
        var team = await TeamAsync();
        var issue = await IssueAsync(_ana, team.Id, "dedupe");
        await _ben.PutAsync($"/api/issues/{issue.Id}/subscription", null);
        var benBefore = await UnreadAsync(_ben);

        // A single PUT that both assigns ben and edits the title: only the
        // assignment is notable, so exactly one notification.
        await _ana.PutAsJsonAsync($"/api/issues/{issue.Id}",
            new { title = "dedupe edited", assignee = "ben" });

        Assert.Equal(benBefore + 1, await UnreadAsync(_ben));
    }

    // ---- Authorization ----

    [Fact]
    public async Task Subscribing_to_a_foreign_teams_issue_is_out_of_reach()
    {
        var teams = await _ana.GetFromJsonAsync<List<TeamPayload>>("/api/teams");
        var eng = teams!.Single(t => t.Key == "ENG");
        var issue = await IssueAsync(_ana, eng.Id, "engineering only");
        var foreigner = _factory.CreateDesMemberClient();

        Assert.Equal(HttpStatusCode.NotFound, (await foreigner.GetAsync($"/api/issues/{issue.Id}/subscription")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreigner.PutAsync($"/api/issues/{issue.Id}/subscription", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreigner.GetAsync($"/api/issues/{issue.Id}/subscribers")).StatusCode);
    }

    [Fact]
    public async Task The_inbox_needs_a_credential()
    {
        var anonymous = _factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications/unread-count")).StatusCode);
    }
}
