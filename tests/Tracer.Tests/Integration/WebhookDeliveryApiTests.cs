using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tracer.Domain;
using Tracer.Infrastructure;

namespace Tracer.Tests.Integration;

/// <summary>
/// Sending: signing, retry, failure classification, and the delivery log.
///
/// <para>
/// <b>Each test gets its own app</b>, unlike every other suite here. The outbox
/// is global state: a delivery another test queued and never drained is still
/// due, so on a shared database it would ride along in this test's drain, eat
/// this test's scripted 503, and turn up in this test's captured requests. Every
/// assertion would have to soften into "at least one" — exactly the kind of
/// vague test that stops catching things. The cost is a few seconds of boot per
/// test; the benefit is that "exactly one delivery went out, and here is what it
/// said" can be asserted and meant.
/// </para>
/// <para>
/// Delivery is driven explicitly through <c>DrainWebhooksAsync</c> rather than by
/// waiting on the background worker, so "did it send?" has an answer rather than
/// a race.
/// </para>
/// </summary>
public class WebhookDeliveryApiTests : IDisposable
{
    private readonly TracerApiFactory _factory;
    private readonly HttpClient _client;
    private readonly StubWebhookEndpoint _endpoint;

    public WebhookDeliveryApiTests()
    {
        _factory = new TracerApiFactory();
        _client = _factory.CreateAdminClient();
        _endpoint = _factory.WebhookEndpoint;
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
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

    // ---- Signing and sending ----

    [Fact]
    public async Task Rotating_the_secret_changes_what_signs_the_next_payload()
    {
        var team = await CreateTeamAsync("Hook F", "HKF");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");

        var rotated = await (await _client.PostAsync($"/api/webhooks/{webhook.Id}/rotate-secret", null))
            .Content.ReadFromJsonAsync<SecretPayload>();
        Assert.NotEqual(webhook.Secret, rotated!.Secret);

        await CreateIssueAsync(team.Id, "signed with the new one");
        await _factory.DrainWebhooksAsync();

        var sent = _endpoint.LastRequest;
        Assert.True(Verify(rotated.Secret, sent), "payload should verify against the rotated secret");
        Assert.False(Verify(webhook.Secret, sent), "the old secret must stop working");
    }



    [Fact]
    public async Task A_delivery_is_signed_and_carries_its_headers()
    {
        var team = await CreateTeamAsync("Hook O", "HKO");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        var issue = await CreateIssueAsync(team.Id, "signed");

        Assert.Equal(1, await _factory.DrainWebhooksAsync());

        var sent = _endpoint.LastRequest;
        Assert.Equal("https://hooks.example.com/tracer", sent.Url);
        Assert.Equal("issue.created", sent.Header("X-Tracer-Event"));
        Assert.Equal("1", sent.Header("X-Tracer-Attempt"));
        Assert.NotNull(sent.Header("X-Tracer-Delivery"));
        Assert.True(Verify(webhook.Secret, sent), "the payload must verify against the webhook's secret");

        var payload = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal("issue.created", payload.GetProperty("event").GetString());
        Assert.Equal("ana", payload.GetProperty("actor").GetString());
        Assert.Equal(issue.Identifier, payload.GetProperty("issue").GetProperty("identifier").GetString());
        Assert.Equal("signed", payload.GetProperty("issue").GetProperty("title").GetString());
    }

    /// <summary>
    /// The body's id is the event, not the envelope: stable across retries and
    /// identical for every subscriber, which is exactly what a consumer needs to
    /// deduplicate. At-least-once is a promise to send, not to send once.
    /// </summary>
    [Fact]
    public async Task Every_subscriber_gets_the_same_event_id_for_one_change()
    {
        var team = await CreateTeamAsync("Hook P", "HKP");
        await SubscribeAsync(team.Id, "IssueCreated");
        await SubscribeAsync(team.Id, "IssueCreated");

        await CreateIssueAsync(team.Id, "one fact");
        Assert.Equal(2, await _factory.DrainWebhooksAsync());

        var ids = _endpoint.Requests
            .Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("id").GetString())
            .ToList();
        Assert.Single(ids.Distinct());

        // ...while the envelope id differs per delivery.
        var deliveryIds = _endpoint.Requests.Select(r => r.Header("X-Tracer-Delivery")).ToList();
        Assert.Equal(2, deliveryIds.Distinct().Count());
    }

    [Fact]
    public async Task A_successful_delivery_is_recorded_as_delivered()
    {
        var team = await CreateTeamAsync("Hook Q", "HKQ");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        await CreateIssueAsync(team.Id, "happy path");

        await _factory.DrainWebhooksAsync();

        var delivery = Assert.Single(await DeliveriesAsync(webhook.Id));
        Assert.Equal("Delivered", delivery.Status);
        Assert.Equal(200, delivery.ResponseStatusCode);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.NotNull(delivery.DeliveredAt);
        Assert.Equal("None", delivery.FailureClass);
    }

    [Fact]
    public async Task A_delivered_event_is_not_sent_again_on_the_next_drain()
    {
        var team = await CreateTeamAsync("Hook R", "HKR");
        await SubscribeAsync(team.Id, "IssueCreated");
        await CreateIssueAsync(team.Id, "once");

        Assert.Equal(1, await _factory.DrainWebhooksAsync());
        Assert.Equal(0, await _factory.DrainWebhooksAsync());
    }

    // ---- Retry and failure classification ----

    [Fact]
    public async Task A_transient_failure_stays_pending_and_backs_off()
    {
        var team = await CreateTeamAsync("Hook S", "HKS");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        _endpoint.Respond(HttpStatusCode.ServiceUnavailable);
        await CreateIssueAsync(team.Id, "deploying");

        var before = DateTimeOffset.UtcNow;
        await _factory.DrainWebhooksAsync();

        var delivery = Assert.Single(await DeliveriesAsync(webhook.Id));
        Assert.Equal("Pending", delivery.Status);
        Assert.Equal("Transient", delivery.FailureClass);
        Assert.Equal(503, delivery.ResponseStatusCode);
        Assert.Equal(1, delivery.AttemptCount);
        // Backed off rather than hammering an endpoint that is already struggling.
        Assert.True(delivery.NextAttemptAt > before, "next attempt should be scheduled into the future");
    }

    [Fact]
    public async Task A_backed_off_delivery_is_not_picked_up_before_it_is_due()
    {
        var team = await CreateTeamAsync("Hook T", "HKT");
        await SubscribeAsync(team.Id, "IssueCreated");
        _endpoint.Respond(HttpStatusCode.ServiceUnavailable);
        await CreateIssueAsync(team.Id, "waiting");

        Assert.Equal(1, await _factory.DrainWebhooksAsync());
        // Immediately after: still backing off, so nothing is due.
        Assert.Equal(0, await _factory.DrainWebhooksAsync());
    }

    /// <summary>
    /// The whole reason for classification. Retrying a 404 would turn one team's
    /// typo into load everyone pays for, and bury the real failures.
    /// </summary>
    [Fact]
    public async Task A_permanent_failure_gives_up_after_one_attempt()
    {
        var team = await CreateTeamAsync("Hook U", "HKU");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        _endpoint.Respond(HttpStatusCode.NotFound);
        await CreateIssueAsync(team.Id, "wrong url");

        await _factory.DrainWebhooksAsync();

        var delivery = Assert.Single(await DeliveriesAsync(webhook.Id));
        Assert.Equal("Failed", delivery.Status);
        Assert.Equal("Permanent", delivery.FailureClass);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(404, delivery.ResponseStatusCode);

        // And never tried again.
        Assert.Equal(0, await _factory.DrainWebhooksAsync());
    }

    [Fact]
    public async Task Rate_limiting_is_retried_even_though_it_is_a_4xx()
    {
        var team = await CreateTeamAsync("Hook V", "HKV");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        _endpoint.Respond(HttpStatusCode.TooManyRequests);
        await CreateIssueAsync(team.Id, "busy receiver");

        await _factory.DrainWebhooksAsync();

        var delivery = Assert.Single(await DeliveriesAsync(webhook.Id));
        Assert.Equal("Pending", delivery.Status);
        Assert.Equal("Transient", delivery.FailureClass);
    }

    [Fact]
    public async Task A_refused_connection_is_transient_and_recorded_without_a_status()
    {
        var team = await CreateTeamAsync("Hook W", "HKW");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        _endpoint.RespondWithNetworkFailure("Connection refused");
        await CreateIssueAsync(team.Id, "nothing listening");

        await _factory.DrainWebhooksAsync();

        var delivery = Assert.Single(await DeliveriesAsync(webhook.Id));
        Assert.Equal("Pending", delivery.Status);
        Assert.Equal("Transient", delivery.FailureClass);
        Assert.Null(delivery.ResponseStatusCode); // never reached the endpoint
        Assert.Contains("refused", delivery.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_timeout_is_transient()
    {
        var team = await CreateTeamAsync("Hook X", "HKX");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        _endpoint.RespondWithTimeout();
        await CreateIssueAsync(team.Id, "black hole");

        await _factory.DrainWebhooksAsync();

        var delivery = Assert.Single(await DeliveriesAsync(webhook.Id));
        Assert.Equal("Transient", delivery.FailureClass);
        Assert.Equal("Timed out.", delivery.Error);
    }

    [Fact]
    public async Task A_recovered_endpoint_gets_the_event_it_missed()
    {
        var team = await CreateTeamAsync("Hook Y", "HKY");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        _endpoint.Respond(HttpStatusCode.ServiceUnavailable);
        await CreateIssueAsync(team.Id, "survives a deploy");
        await _factory.DrainWebhooksAsync();

        // Wind the backoff back so the retry is due, exactly as time passing would.
        await MakeDueAsync(webhook.Id);
        Assert.Equal(1, await _factory.DrainWebhooksAsync());

        var delivery = Assert.Single(await DeliveriesAsync(webhook.Id));
        Assert.Equal("Delivered", delivery.Status);
        Assert.Equal(2, delivery.AttemptCount);
        Assert.Equal(2, _endpoint.Requests.Count);
    }

    /// <summary>The retry sends the same frozen bytes, so the signature still matches them.</summary>
    [Fact]
    public async Task A_retry_re_signs_the_identical_payload()
    {
        var team = await CreateTeamAsync("Hook Z", "HKZ");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        _endpoint.Respond(HttpStatusCode.ServiceUnavailable);
        var issue = await CreateIssueAsync(team.Id, "frozen");
        await _factory.DrainWebhooksAsync();

        // The issue changes in between; the queued event must not.
        await _client.PutAsJsonAsync($"/api/issues/{issue.Id}", new { title = "renamed since" });

        await MakeDueAsync(webhook.Id);
        await _factory.DrainWebhooksAsync();

        var attempts = _endpoint.Requests.ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal(attempts[0].Body, attempts[1].Body);
        Assert.Equal("frozen", JsonDocument.Parse(attempts[1].Body)
            .RootElement.GetProperty("issue").GetProperty("title").GetString());
        Assert.Equal("2", attempts[1].Header("X-Tracer-Attempt"));
        Assert.True(Verify(webhook.Secret, attempts[1]), "the retry must still verify");
    }

    [Fact]
    public async Task A_delivery_gives_up_once_the_attempts_run_out()
    {
        var team = await CreateTeamAsync("Hook AA", "HAA");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        _endpoint.DefaultStatus = HttpStatusCode.ServiceUnavailable;
        await CreateIssueAsync(team.Id, "never coming back");

        for (var attempt = 0; attempt < WebhookRetryPolicy.MaxAttempts; attempt++)
        {
            await MakeDueAsync(webhook.Id);
            await _factory.DrainWebhooksAsync();
        }

        var delivery = Assert.Single(await DeliveriesAsync(webhook.Id));
        Assert.Equal("Failed", delivery.Status);
        Assert.Equal(WebhookRetryPolicy.MaxAttempts, delivery.AttemptCount);
        Assert.Equal("Transient", delivery.FailureClass);

        // Exhausted means exhausted.
        await MakeDueAsync(webhook.Id);
        Assert.Equal(0, await _factory.DrainWebhooksAsync());
    }

    // ---- The delivery log ----

    [Fact]
    public async Task The_delivery_log_filters_by_status()
    {
        var team = await CreateTeamAsync("Hook AB", "HAB");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        _endpoint.Respond(HttpStatusCode.NotFound);
        await CreateIssueAsync(team.Id, "fails");
        await _factory.DrainWebhooksAsync();
        await CreateIssueAsync(team.Id, "succeeds");
        await _factory.DrainWebhooksAsync();

        var failed = await _client.GetFromJsonAsync<PagedDeliveries>(
            $"/api/webhooks/{webhook.Id}/deliveries?status=Failed");
        var delivered = await _client.GetFromJsonAsync<PagedDeliveries>(
            $"/api/webhooks/{webhook.Id}/deliveries?status=Delivered");

        Assert.Single(failed!.Items);
        Assert.Single(delivered!.Items);
    }

    [Fact]
    public async Task A_delivery_can_be_inspected_with_the_bytes_that_were_sent()
    {
        var team = await CreateTeamAsync("Hook AC", "HAC");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        await CreateIssueAsync(team.Id, "inspect me");
        await _factory.DrainWebhooksAsync();
        var delivery = (await DeliveriesAsync(webhook.Id)).Single();

        var detail = await _client.GetFromJsonAsync<DeliveryDetailPayload>(
            $"/api/webhooks/{webhook.Id}/deliveries/{delivery.Id}");

        Assert.Equal(_endpoint.LastRequest.Body, detail!.Payload);
    }

    [Fact]
    public async Task A_failed_delivery_can_be_sent_again_by_hand()
    {
        var team = await CreateTeamAsync("Hook AD", "HAD");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        _endpoint.Respond(HttpStatusCode.NotFound);
        await CreateIssueAsync(team.Id, "typo in the url");
        await _factory.DrainWebhooksAsync();
        var failed = (await DeliveriesAsync(webhook.Id)).Single();
        Assert.Equal("Failed", failed.Status);

        var response = await _client.PostAsync(
            $"/api/webhooks/{webhook.Id}/deliveries/{failed.Id}/redeliver", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, await _factory.DrainWebhooksAsync());
        var retried = (await DeliveriesAsync(webhook.Id)).Single();
        Assert.Equal("Delivered", retried.Status);
    }

    [Fact]
    public async Task Redelivering_something_still_pending_is_refused()
    {
        var team = await CreateTeamAsync("Hook AE", "HAE");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        await CreateIssueAsync(team.Id, "not yet sent");
        var pending = (await DeliveriesAsync(webhook.Id)).Single();

        var response = await _client.PostAsync(
            $"/api/webhooks/{webhook.Id}/deliveries/{pending.Id}/redeliver", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_webhook_takes_its_delivery_log_with_it()
    {
        var team = await CreateTeamAsync("Hook AF", "HAF");
        var webhook = await SubscribeAsync(team.Id, "IssueCreated");
        await CreateIssueAsync(team.Id, "logged");

        await _client.DeleteAsync($"/api/webhooks/{webhook.Id}");

        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/webhooks/{webhook.Id}/deliveries")).StatusCode);
    }


    /// <summary>Verifies a captured request the way a real consumer would.</summary>
    private static bool Verify(string secret, CapturedWebhookRequest request) =>
        WebhookSignature.Verify(
            secret,
            request.Header(WebhookSignature.HeaderName) ?? string.Empty,
            request.Body,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));

    /// <summary>
    /// Makes a webhook's pending deliveries due now, standing in for the passage
    /// of time. Sleeping through a real exponential backoff would add minutes to
    /// the suite to test arithmetic the unit tests already cover.
    /// </summary>
    private async Task MakeDueAsync(Guid webhookId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TracerDbContext>();
        await db.WebhookDeliveries
            .Where(d => d.WebhookId == webhookId)
            .ExecuteUpdateAsync(d => d.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
    }
}
