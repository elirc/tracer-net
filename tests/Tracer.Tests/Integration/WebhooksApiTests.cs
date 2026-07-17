using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// Webhook subscriptions and what a change queues.
///
/// These share one app through <c>IClassFixture</c>, because none of them drain
/// the outbox, script a response, or read what was sent — they assert on
/// delivery *rows*, which are scoped to their own webhook and cannot be
/// disturbed by another test. The tests that do touch the shared sending
/// machinery live in <see cref="WebhookDeliveryApiTests"/>, which pays for
/// isolation because it needs it.
/// </summary>
public class WebhooksApiTests : IClassFixture<TracerApiFactory>
{
    private readonly TracerApiFactory _factory;
    private readonly HttpClient _client;

    public WebhooksApiTests(TracerApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAdminClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record IssuePayload(Guid Id, string Identifier, string Title, Guid StateId);
    private sealed record StatePayload(Guid Id, string Name, string Type);
    private sealed record WebhookPayload(Guid Id, Guid TeamId, string Name, string Url, List<string> Events, bool IsActive);
    private sealed record CreatedWebhookPayload(Guid Id, string Name, string Url, List<string> Events, string Secret);
    private sealed record SecretPayload(Guid Id, string Secret);
    private sealed record DeliveryPayload(
        Guid Id,
        Guid WebhookId,
        string Event,
        string Status,
        int AttemptCount,
        int? ResponseStatusCode,
        string FailureClass,
        string? Error,
        DateTimeOffset? DeliveredAt,
        DateTimeOffset NextAttemptAt);
    private sealed record DeliveryDetailPayload(DeliveryPayload Delivery, string Payload);
    private sealed record PagedDeliveries(List<DeliveryPayload> Items, int Total);

    private async Task<TeamPayload> CreateTeamAsync(string name, string key)
    {
        var created = await _client.PostAsJsonAsync("/api/teams", new { name, key });
        return (await created.Content.ReadFromJsonAsync<TeamPayload>())!;
    }

    private async Task<CreatedWebhookPayload> SubscribeAsync(Guid teamId, params string[] events)
    {
        var created = await _client.PostAsJsonAsync($"/api/teams/{teamId}/webhooks", new
        {
            name = "test hook",
            url = "https://hooks.example.com/tracer",
            events = events.Length == 0 ? ["IssueCreated"] : events,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<CreatedWebhookPayload>())!;
    }

    private async Task<IssuePayload> CreateIssueAsync(Guid teamId, string title)
    {
        var created = await _client.PostAsJsonAsync($"/api/teams/{teamId}/issues", new { title });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    private async Task<List<DeliveryPayload>> DeliveriesAsync(Guid webhookId) =>
        (await _client.GetFromJsonAsync<PagedDeliveries>($"/api/webhooks/{webhookId}/deliveries"))!.Items;

    // ---- Subscribing ----

    [Fact]
    public async Task Creating_a_webhook_returns_its_secret_exactly_once()
    {
        var team = await CreateTeamAsync("Hook A", "HKA");

        var created = await SubscribeAsync(team.Id, "IssueCreated");
        Assert.NotEmpty(created.Secret);

        // Never echoed by a read, because storage cannot protect it — signing
        // needs the secret itself, so exposure is what gets limited.
        var body = await (await _client.GetAsync($"/api/webhooks/{created.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(created.Secret, body);

        var listed = await (await _client.GetAsync($"/api/teams/{team.Id}/webhooks")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(created.Secret, listed);
    }

    [Fact]
    public async Task A_webhook_url_must_not_point_inside_the_network()
    {
        var team = await CreateTeamAsync("Hook B", "HKB");

        var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/webhooks", new
        {
            name = "ssrf",
            url = "http://169.254.169.254/latest/meta-data/",
            events = new[] { "IssueCreated" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_webhook_needs_at_least_one_event()
    {
        var team = await CreateTeamAsync("Hook C", "HKC");

        var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/webhooks", new
        {
            name = "silent",
            url = "https://hooks.example.com/tracer",
            events = Array.Empty<string>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Subscribing_to_the_same_event_twice_does_not_double_deliver()
    {
        var team = await CreateTeamAsync("Hook D", "HKD");
        var created = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/webhooks", new
        {
            name = "dupe events",
            url = "https://hooks.example.com/tracer",
            events = new[] { "IssueCreated", "IssueCreated" },
        });
        var webhook = (await created.Content.ReadFromJsonAsync<CreatedWebhookPayload>())!;

        await CreateIssueAsync(team.Id, "once please");

        Assert.Equal(["IssueCreated"], webhook.Events.ToArray());
        Assert.Single(await DeliveriesAsync(webhook.Id));
    }

    [Fact]
    public async Task Editing_a_webhooks_events_actually_persists()
    {
        // The Events list rides a value converter; without a value comparer EF
        // compares it by reference, decides nothing changed, and silently drops
        // the edit.
        var team = await CreateTeamAsync("Hook E", "HKE");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");

        await _client.PutAsJsonAsync($"/api/webhooks/{webhook.Id}", new
        {
            name = "test hook",
            url = "https://hooks.example.com/tracer",
            events = new[] { "IssueUpdated", "CommentCreated" },
            isActive = true,
        });

        var reloaded = await _client.GetFromJsonAsync<WebhookPayload>($"/api/webhooks/{webhook.Id}");
        Assert.Equal(["CommentCreated", "IssueUpdated"], reloaded!.Events.Order().ToArray());
    }

    // ---- Enqueue: the outbox ----

    [Fact]
    public async Task A_change_queues_a_delivery_only_for_subscribed_events()
    {
        var team = await CreateTeamAsync("Hook G", "HKG");
        var webhook = await SubscribeAsync(team.Id, "CommentCreated");

        // Not subscribed: no delivery.
        var issue = await CreateIssueAsync(team.Id, "quiet");
        Assert.Empty(await DeliveriesAsync(webhook.Id));

        // Subscribed: one delivery.
        await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/comments", new { body = "hello" });
        var delivery = Assert.Single(await DeliveriesAsync(webhook.Id));
        Assert.Equal("CommentCreated", delivery.Event);
    }

    [Fact]
    public async Task An_inactive_webhook_queues_nothing()
    {
        var team = await CreateTeamAsync("Hook H", "HKH");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        await _client.PutAsJsonAsync($"/api/webhooks/{webhook.Id}", new
        {
            name = "test hook",
            url = "https://hooks.example.com/tracer",
            events = new[] { "IssueCreated" },
            isActive = false,
        });

        await CreateIssueAsync(team.Id, "nobody is listening");

        Assert.Empty(await DeliveriesAsync(webhook.Id));
    }

    [Fact]
    public async Task Another_teams_changes_never_reach_this_teams_webhook()
    {
        var mine = await CreateTeamAsync("Hook I", "HKI");
        var theirs = await CreateTeamAsync("Hook J", "HKJ");
        var webhook = await SubscribeAsync(mine.Id, "IssueCreated");

        await CreateIssueAsync(theirs.Id, "not your business");

        Assert.Empty(await DeliveriesAsync(webhook.Id));
    }

    [Fact]
    public async Task Every_kind_of_edit_arrives_as_issue_updated()
    {
        var team = await CreateTeamAsync("Hook K", "HKK");
        var webhook = await SubscribeAsync(team.Id, "IssueUpdated");
        var issue = await CreateIssueAsync(team.Id, "edited");

        await _client.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "edited", assignee = "ben" });

        var delivery = Assert.Single(await DeliveriesAsync(webhook.Id));
        Assert.Equal("IssueUpdated", delivery.Event);
    }

    [Fact]
    public async Task A_state_change_is_its_own_event_not_an_update()
    {
        var team = await CreateTeamAsync("Hook L", "HKL");
        var webhook = await SubscribeAsync(team.Id, "IssueStateChanged");
        var issue = await CreateIssueAsync(team.Id, "moving");
        var todo = (await _client.GetFromJsonAsync<List<StatePayload>>($"/api/teams/{team.Id}/states"))!
            .Single(s => s.Type == "Todo");

        await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/transitions", new { stateId = todo.Id });

        var delivery = Assert.Single(await DeliveriesAsync(webhook.Id));
        Assert.Equal("IssueStateChanged", delivery.Event);
    }

    /// <summary>
    /// Deleting an issue is recorded in the audit log but is not a subscribable
    /// event: unmapped means silent, so the log can grow types without the public
    /// contract growing with it.
    /// </summary>
    [Fact]
    public async Task An_unmapped_change_announces_nothing()
    {
        var team = await CreateTeamAsync("Hook M", "HKM");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated", "IssueUpdated", "IssueStateChanged", "CommentCreated");
        var issue = await CreateIssueAsync(team.Id, "doomed");
        var before = (await DeliveriesAsync(webhook.Id)).Count;

        await _client.DeleteAsync($"/api/issues/{issue.Id}");

        Assert.Equal(before, (await DeliveriesAsync(webhook.Id)).Count);
    }

    [Fact]
    public async Task Two_webhooks_on_one_event_each_get_their_own_delivery()
    {
        var team = await CreateTeamAsync("Hook N", "HKN");
        var first = await SubscribeAsync(team.Id, "IssueCreated");
        var second = await SubscribeAsync(team.Id, "IssueCreated");

        await CreateIssueAsync(team.Id, "fan out");

        Assert.Single(await DeliveriesAsync(first.Id));
        Assert.Single(await DeliveriesAsync(second.Id));
    }

    // ---- Authorization ----

    [Fact]
    public async Task Another_teams_webhooks_are_out_of_reach()
    {
        var teams = await _client.GetListAsync<TeamPayload>("/api/teams");
        var eng = teams!.Single(t => t.Key == "ENG");
        var webhook = await SubscribeAsync(eng.Id, "IssueCreated");
        var foreigner = _factory.CreateDesMemberClient();

        Assert.Equal(HttpStatusCode.NotFound, (await foreigner.GetAsync($"/api/teams/{eng.Id}/webhooks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreigner.GetAsync($"/api/webhooks/{webhook.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await foreigner.GetAsync($"/api/webhooks/{webhook.Id}/deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await foreigner.PostAsync($"/api/webhooks/{webhook.Id}/rotate-secret", null)).StatusCode);
    }
}
