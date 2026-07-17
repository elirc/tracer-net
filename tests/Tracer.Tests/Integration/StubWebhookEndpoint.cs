using System.Collections.Concurrent;
using System.Net;

namespace Tracer.Tests.Integration;

/// <summary>One request this API sent to a subscriber's endpoint, captured whole.</summary>
public sealed record CapturedWebhookRequest(
    string Url,
    string Body,
    IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Stands in for a subscriber's server.
///
/// <para>
/// A stub handler rather than a real socket on localhost, for two reasons. It
/// makes "what does the sender do when the endpoint returns 500 three times and
/// then 200?" an ordinary thing to write, where a real server would need
/// orchestrating into each state. And <see cref="Tracer.Domain.WebhookUrlPolicy"/>
/// refuses loopback addresses on purpose — so a real local receiver could not be
/// registered here anyway without punching a hole in the SSRF control being
/// tested.
/// </para>
/// </summary>
public sealed class StubWebhookEndpoint : HttpMessageHandler
{
    private readonly ConcurrentQueue<CapturedWebhookRequest> _requests = new();
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _scripted = new();

    /// <summary>What every unscripted request gets. 200 by default.</summary>
    public HttpStatusCode DefaultStatus { get; set; } = HttpStatusCode.OK;

    public IReadOnlyList<CapturedWebhookRequest> Requests => [.. _requests];

    public CapturedWebhookRequest LastRequest => _requests.Last();

    /// <summary>Queues one response, consumed by the next request. Lets a test spell out a sequence.</summary>
    public void Respond(HttpStatusCode status) =>
        _scripted.Enqueue(() => new HttpResponseMessage(status));

    /// <summary>Queues a thrown exception — a refused connection, DNS failure, or the like.</summary>
    public void RespondWithNetworkFailure(string message = "Connection refused") =>
        _scripted.Enqueue(() => throw new HttpRequestException(message));

    /// <summary>Queues a timeout, which surfaces the way <see cref="HttpClient"/>'s does.</summary>
    public void RespondWithTimeout() =>
        _scripted.Enqueue(() => throw new TaskCanceledException("The request timed out."));

    public void Reset()
    {
        _requests.Clear();
        _scripted.Clear();
        DefaultStatus = HttpStatusCode.OK;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

        var headers = request.Headers
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
        }

        _requests.Enqueue(new CapturedWebhookRequest(request.RequestUri!.ToString(), body, headers));

        // Captured before the scripted response can throw: a test asserting on
        // what was sent to an endpoint that refused the connection still needs
        // to see the request.
        return _scripted.TryDequeue(out var next)
            ? next()
            : new HttpResponseMessage(DefaultStatus);
    }
}
