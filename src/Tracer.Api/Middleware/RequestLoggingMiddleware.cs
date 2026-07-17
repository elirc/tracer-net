using System.Diagnostics;

namespace Tracer.Api.Middleware;

/// <summary>
/// Logs one structured line per request: method, path, status, and how long it
/// took. One line in, one line out is the cheapest thing that turns "the API is
/// slow" or "someone is getting 403s" into something answerable — without it, the
/// only record of what the server did is whatever the client happened to keep.
///
/// <para>
/// It sits at the very front of the pipeline so the elapsed time covers
/// everything downstream — auth, model binding, the handler, and the error
/// middleware that turns a throw into a 500 — rather than only the part after
/// they have run. It logs in a <c>finally</c> for the same reason: a request that
/// blows up is exactly the one worth a line, so the status is read after the rest
/// of the pipeline has settled on it, even when that settling was an exception.
/// </para>
/// </summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;
            logger.LogInformation(
                "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs:0.0} ms",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                elapsedMs);
        }
    }
}

public static class RequestLoggingMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestLoggingMiddleware>();
}
