using Tracer.Domain;
using Tracer.Domain.Entities;

namespace Tracer.Tests.Unit;

public class WebhookRetryPolicyTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(204)]
    public void A_2xx_is_not_a_failure(int status)
    {
        Assert.Equal(WebhookFailureClass.None, WebhookRetryPolicy.Classify(status));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)] // the deploy-window case retries exist for
    public void A_5xx_is_transient(int status)
    {
        Assert.Equal(WebhookFailureClass.Transient, WebhookRetryPolicy.Classify(status));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(410)]
    public void A_4xx_is_permanent(int status)
    {
        // Asking a thousand more times will not make the route exist.
        Assert.Equal(WebhookFailureClass.Permanent, WebhookRetryPolicy.Classify(status));
    }

    /// <summary>
    /// The one 4xx that means "come back later" rather than "no". Treating the
    /// whole range as permanent would drop events for a receiver that was merely
    /// busy.
    /// </summary>
    [Fact]
    public void Rate_limiting_is_transient_despite_being_a_4xx()
    {
        Assert.Equal(WebhookFailureClass.Transient, WebhookRetryPolicy.Classify(429));
    }

    [Fact]
    public void A_request_timeout_is_transient()
    {
        Assert.Equal(WebhookFailureClass.Transient, WebhookRetryPolicy.Classify(408));
    }

    /// <summary>No status at all — timeout, refused connection, DNS — is the most transient thing there is.</summary>
    [Fact]
    public void No_response_at_all_is_transient()
    {
        Assert.Equal(WebhookFailureClass.Transient, WebhookRetryPolicy.Classify(null));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    public void A_redirect_is_permanent(int status)
    {
        // Following one would forward a signed payload to a host the team never
        // registered; it is a misconfigured endpoint, not an outage.
        Assert.Equal(WebhookFailureClass.Permanent, WebhookRetryPolicy.Classify(status));
    }

    [Fact]
    public void A_permanent_failure_is_never_retried()
    {
        Assert.False(WebhookRetryPolicy.ShouldRetry(WebhookFailureClass.Permanent, attemptCount: 1));
    }

    [Fact]
    public void A_transient_failure_is_retried_until_the_attempts_run_out()
    {
        Assert.True(WebhookRetryPolicy.ShouldRetry(WebhookFailureClass.Transient, 1));
        Assert.True(WebhookRetryPolicy.ShouldRetry(WebhookFailureClass.Transient, WebhookRetryPolicy.MaxAttempts - 1));
        Assert.False(WebhookRetryPolicy.ShouldRetry(WebhookFailureClass.Transient, WebhookRetryPolicy.MaxAttempts));
    }

    /// <summary>
    /// Exponential, not fixed: a fixed interval turns each retry into more load
    /// on an endpoint that is most likely failing because it is overloaded.
    /// </summary>
    [Fact]
    public void Backoff_doubles_with_each_attempt()
    {
        var first = WebhookRetryPolicy.BackoffAfter(1);
        var second = WebhookRetryPolicy.BackoffAfter(2);
        var third = WebhookRetryPolicy.BackoffAfter(3);

        Assert.Equal(second, first * 2);
        Assert.Equal(third, second * 2);
    }

    [Fact]
    public void The_next_attempt_is_scheduled_into_the_future()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(WebhookRetryPolicy.NextAttemptAt(now, 1) > now);
    }
}

public class WebhookEventsTests
{
    [Theory]
    [InlineData(ActivityType.IssueCreated, WebhookEvent.IssueCreated)]
    [InlineData(ActivityType.IssueStateChanged, WebhookEvent.IssueStateChanged)]
    [InlineData(ActivityType.CommentCreated, WebhookEvent.CommentCreated)]
    public void Mapped_activity_types_produce_their_event(ActivityType type, WebhookEvent expected)
    {
        Assert.Equal(expected, WebhookEvents.For(type));
    }

    /// <summary>
    /// A consumer wants to know an issue changed and re-read it; it does not want
    /// a separate event type per field, and we do not want to owe it one forever.
    /// </summary>
    [Theory]
    [InlineData(ActivityType.IssueUpdated)]
    [InlineData(ActivityType.IssueAssigned)]
    [InlineData(ActivityType.IssueLabelAdded)]
    [InlineData(ActivityType.IssueLabelRemoved)]
    [InlineData(ActivityType.IssueRelationAdded)]
    [InlineData(ActivityType.IssueRelationRemoved)]
    [InlineData(ActivityType.IssueParentChanged)]
    public void Every_kind_of_edit_collapses_into_issue_updated(ActivityType type)
    {
        Assert.Equal(WebhookEvent.IssueUpdated, WebhookEvents.For(type));
    }

    /// <summary>
    /// The log may grow new types freely; the event stream is a promise to
    /// strangers. Unmapped means silent, on purpose.
    /// </summary>
    [Theory]
    [InlineData(ActivityType.IssueDeleted)]
    [InlineData(ActivityType.CommentUpdated)]
    [InlineData(ActivityType.CommentDeleted)]
    public void Unmapped_activity_types_announce_nothing(ActivityType type)
    {
        Assert.Null(WebhookEvents.For(type));
    }

    [Theory]
    [InlineData(WebhookEvent.IssueCreated, "issue.created")]
    [InlineData(WebhookEvent.IssueUpdated, "issue.updated")]
    [InlineData(WebhookEvent.IssueStateChanged, "issue.state_changed")]
    [InlineData(WebhookEvent.CommentCreated, "comment.created")]
    public void Events_have_stable_wire_names(WebhookEvent value, string expected)
    {
        Assert.Equal(expected, value.WireName());
    }
}

public class WebhookUrlPolicyTests
{
    [Theory]
    [InlineData("https://hooks.example.com/tracer")]
    [InlineData("http://example.com:8080/hook")]
    [InlineData("https://93.184.216.34/hook")] // a public literal IP is fine
    public void A_public_http_url_is_allowed(string url)
    {
        Assert.True(WebhookUrlPolicy.IsAllowed(url, out _), url);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("example.com/hook")] // no scheme: not absolute
    public void A_malformed_url_is_refused(string url)
    {
        Assert.False(WebhookUrlPolicy.IsAllowed(url, out _), url);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/hook")]
    [InlineData("gopher://example.com/")]
    public void A_non_http_scheme_is_refused(string url)
    {
        // Each is its own way to make the server read something local.
        Assert.False(WebhookUrlPolicy.IsAllowed(url, out _), url);
    }

    /// <summary>
    /// The whole reason this class exists. A webhook URL is attacker-supplied
    /// input the *server* fetches from inside the network.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://localhost/hook")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://192.168.1.10/hook")]
    [InlineData("http://172.16.0.1/hook")]
    [InlineData("http://172.31.255.254/hook")]
    [InlineData("http://100.64.0.1/hook")]
    [InlineData("http://[::1]/hook")]
    public void An_internal_address_is_refused(string url)
    {
        Assert.False(WebhookUrlPolicy.IsAllowed(url, out _), url);
    }

    /// <summary>
    /// 169.254.169.254 is where cloud providers serve instance credentials. This
    /// single line is the difference between a webhook feature and a credential
    /// exfiltration endpoint.
    /// </summary>
    [Fact]
    public void The_cloud_metadata_address_is_refused()
    {
        Assert.False(WebhookUrlPolicy.IsAllowed("http://169.254.169.254/latest/meta-data/", out _));
    }

    [Fact]
    public void Loopback_wearing_an_ipv6_hat_is_refused()
    {
        // ::ffff:127.0.0.1 is 127.0.0.1 with a different spelling.
        Assert.False(WebhookUrlPolicy.IsAllowed("http://[::ffff:127.0.0.1]/hook", out _));
    }

    [Fact]
    public void A_unique_local_ipv6_address_is_refused()
    {
        Assert.False(WebhookUrlPolicy.IsAllowed("http://[fd00::1]/hook", out _));
    }

    [Fact]
    public void A_refusal_explains_itself()
    {
        Assert.False(WebhookUrlPolicy.IsAllowed("http://127.0.0.1/hook", out var reason));
        Assert.NotEmpty(reason);
    }

    /// <summary>
    /// Pins the documented limit rather than pretending it away: a hostname is
    /// only checked when it is a literal IP, so a name that resolves to a private
    /// address passes. Closing this needs a resolve-and-pin at send time, and
    /// this test is here so nobody reads the class and assumes otherwise.
    /// </summary>
    [Fact]
    public void A_hostname_that_resolves_privately_is_a_known_gap()
    {
        Assert.True(WebhookUrlPolicy.IsAllowed("https://internal.example.com/hook", out _));
    }
}

public class WebhookSignatureTests
{
    private const string Secret = "trk_test_secret";
    private const string Payload = """{"id":"1","event":"issue.created"}""";

    [Fact]
    public void A_header_carries_a_timestamp_and_a_versioned_signature()
    {
        var header = WebhookSignature.Header(Secret, DateTimeOffset.UtcNow, Payload);

        Assert.StartsWith("t=", header);
        Assert.Contains(",v1=", header);
    }

    [Fact]
    public void A_signature_verifies_against_the_payload_it_signed()
    {
        var now = DateTimeOffset.UtcNow;
        var header = WebhookSignature.Header(Secret, now, Payload);

        Assert.True(WebhookSignature.Verify(Secret, header, Payload, now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_tampered_payload_does_not_verify()
    {
        var now = DateTimeOffset.UtcNow;
        var header = WebhookSignature.Header(Secret, now, Payload);

        Assert.False(WebhookSignature.Verify(Secret, header, Payload + " ", now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void The_wrong_secret_does_not_verify()
    {
        var now = DateTimeOffset.UtcNow;
        var header = WebhookSignature.Header(Secret, now, Payload);

        Assert.False(WebhookSignature.Verify("trk_other_secret", header, Payload, now, TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// The reason the timestamp is signed rather than sent beside the signature.
    /// Without a bound, a captured request replays forever — perfectly signed.
    /// </summary>
    [Fact]
    public void An_old_signature_is_rejected_however_valid_it_is()
    {
        var signedAt = DateTimeOffset.UtcNow;
        var header = WebhookSignature.Header(Secret, signedAt, Payload);

        var replayedAnHourLater = signedAt.AddHours(1);

        Assert.True(WebhookSignature.Verify(Secret, header, Payload, signedAt, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(Secret, header, Payload, replayedAnHourLater, TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// And the timestamp is inside the signed material, so an attacker cannot
    /// simply refresh it to get past the tolerance check.
    /// </summary>
    [Fact]
    public void Rewriting_the_timestamp_invalidates_the_signature()
    {
        var signedAt = DateTimeOffset.UtcNow;
        var header = WebhookSignature.Header(Secret, signedAt, Payload);
        var signature = header.Split(",v1=")[1];

        var forged = $"t={DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()},v1={signature}";

        Assert.False(WebhookSignature.Verify(
            Secret, forged, Payload, DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1=abc")] // no timestamp
    [InlineData("t=123")]  // no signature
    public void A_malformed_header_does_not_verify(string header)
    {
        Assert.False(WebhookSignature.Verify(Secret, header, Payload, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Signing_is_deterministic_for_the_same_inputs()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        Assert.Equal(
            WebhookSignature.Header(Secret, at, Payload),
            WebhookSignature.Header(Secret, at, Payload));
    }
}
