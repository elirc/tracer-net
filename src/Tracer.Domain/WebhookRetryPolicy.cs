using Tracer.Domain.Entities;

namespace Tracer.Domain;

/// <summary>
/// When to try again, and when to stop.
///
/// <para>
/// The distinction that matters is whether the endpoint *could* succeed later.
/// A 503 during a deploy is the case retries exist for. A 404 is not: the route
/// does not exist, and asking a thousand more times will not create it — it will
/// only turn one team's typo into load everybody pays for, and bury the real
/// failures in a delivery log nobody can read.
/// </para>
/// </summary>
public static class WebhookRetryPolicy
{
    /// <summary>
    /// Attempts before giving up. With the backoff below that spans roughly a
    /// quarter of an hour — long enough to ride out a deploy or a restart,
    /// short enough that a dead endpoint stops costing anything by lunchtime.
    /// </summary>
    public const int MaxAttempts = 5;

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How a response decides the delivery's fate. A null status means the
    /// request never got an answer at all — a timeout, a refused connection, DNS
    /// failing — which is the most transient thing there is.
    /// </summary>
    public static WebhookFailureClass Classify(int? statusCode) => statusCode switch
    {
        null => WebhookFailureClass.Transient,
        >= 200 and < 300 => WebhookFailureClass.None,

        // Rate limiting is the one 4xx that is a request to come back later
        // rather than a refusal, so it retries. Treating the whole 4xx range as
        // permanent would drop events for a receiver that was merely busy.
        429 => WebhookFailureClass.Transient,

        // 408 is the server saying it gave up waiting; the next attempt may land.
        408 => WebhookFailureClass.Transient,

        >= 400 and < 500 => WebhookFailureClass.Permanent,
        >= 500 => WebhookFailureClass.Transient,

        // 1xx/3xx to a POSTed webhook is a misconfigured endpoint, not an
        // outage: a redirect chain silently following to some other host is
        // exactly what nobody wants from a signed payload.
        _ => WebhookFailureClass.Permanent,
    };

    /// <summary>
    /// True when another attempt is worth making: the failure could clear, and
    /// there are attempts left.
    /// </summary>
    public static bool ShouldRetry(WebhookFailureClass failure, int attemptCount) =>
        failure == WebhookFailureClass.Transient && attemptCount < MaxAttempts;

    /// <summary>
    /// Exponential backoff: 10s, 20s, 40s, 80s. Exponential rather than fixed
    /// because a fixed interval turns every one of our retries into another
    /// request against an endpoint that is already failing — most likely because
    /// it is overloaded. Backing off is how retrying helps rather than piles on.
    /// </summary>
    public static TimeSpan BackoffAfter(int attemptCount) =>
        FirstBackoff * Math.Pow(2, Math.Max(0, attemptCount - 1));

    /// <summary>When the next attempt becomes due.</summary>
    public static DateTimeOffset NextAttemptAt(DateTimeOffset now, int attemptCount) =>
        now + BackoffAfter(attemptCount);
}
