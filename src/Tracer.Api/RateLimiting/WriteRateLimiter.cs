using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Tracer.Api.Auth;

namespace Tracer.Api.RateLimiting;

/// <summary>
/// A global rate limiter aimed at the endpoints that change state.
///
/// <para>
/// <b>Only writes are limited.</b> A read is cheap and idempotent; a burst of
/// <c>GET</c>s is a client that is merely eager. A burst of <c>POST</c>/<c>PUT</c>/
/// <c>DELETE</c>s is the shape of a runaway script or a retry storm, and each one
/// costs a transaction, an audit entry, and — through the activity spine — webhook
/// deliveries and notifications fanned out to everyone watching. So reads pass
/// through unmetered and only mutating methods draw from the bucket.
/// </para>
/// <para>
/// <b>The bucket is per credential, not per process.</b> Partitioning by API key
/// means one team's misbehaving integration cannot spend everyone else's budget;
/// an unauthenticated caller (which can only reach <c>/api/health</c>) is bucketed
/// by remote address instead. The limit is a fixed window read from configuration
/// so an operator can tighten or loosen it without a redeploy.
/// </para>
/// </summary>
public static class WriteRateLimiter
{
    public const string PolicyName = "writes";

    public static IServiceCollection AddWriteRateLimiter(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!IsWrite(context.Request.Method))
                {
                    // Reads are never throttled: a NoLimiter partition draws from
                    // nothing and rejects nothing.
                    return RateLimitPartition.GetNoLimiter("read");
                }

                // Read lazily from the request's configuration rather than captured
                // at registration, so the value reflects the final merged config —
                // which is what lets a host (or a test) set the limit without this
                // code caring where it came from.
                var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
                var permitLimit = configuration.GetValue<int?>("RateLimiting:PermitLimit") ?? 120;
                var windowSeconds = configuration.GetValue<int?>("RateLimiting:WindowSeconds") ?? 60;

                var partitionKey = context.Request.Headers[ApiKeyAuthenticationHandler.HeaderName].ToString();
                if (string.IsNullOrEmpty(partitionKey))
                {
                    partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromSeconds(windowSeconds),
                        QueueLimit = 0,
                    });
            });

            // A rejection is an error like any other, so it comes back as RFC 7807
            // rather than an empty 429 the client has to guess at. Retry-After is
            // set when the window's reset time is known, so a well-behaved client
            // backs off for exactly as long as it needs to.
            options.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }

                var problemService = context.HttpContext.RequestServices
                    .GetRequiredService<IProblemDetailsService>();

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await problemService.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests.",
                        Detail = "You have made too many write requests in a short window. Slow down and try again shortly.",
                    },
                });
            };
        });

        return services;
    }

    private static bool IsWrite(string method) =>
        HttpMethods.IsPost(method)
        || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method)
        || HttpMethods.IsDelete(method);
}
