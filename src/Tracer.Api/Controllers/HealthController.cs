using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// Liveness and readiness. The one endpoint that opts out of the fallback
/// authorization policy: a load balancer has no credential, and a probe that
/// needs one stops being a probe of the app and starts being a probe of the key
/// store.
///
/// <para>
/// The probe actually touches the database rather than just returning 200 the
/// moment the process is up. A web host that has lost its database is not healthy
/// — it will fail every real request — so an orchestrator that reads this needs
/// the answer to include whether the data layer is reachable. When it is not, the
/// whole check reports <c>503</c> so the instance is pulled from rotation.
/// </para>
/// </summary>
[ApiController]
[Route("api/health")]
[AllowAnonymous]
public class HealthController(TracerDbContext db) : ControllerBase
{
    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    [HttpGet]
    public async Task<ActionResult<HealthResponse>> Get(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        bool databaseHealthy;
        try
        {
            // A real round trip, not just "is a connection object configured".
            databaseHealthy = await db.Database.CanConnectAsync(ct);
        }
        catch
        {
            // A probe that throws is a probe that answers "unhealthy", not one
            // that 500s: the whole reason to call this is to find out, so it must
            // return a body either way.
            databaseHealthy = false;
        }
        stopwatch.Stop();

        var response = new HealthResponse(
            Status: databaseHealthy ? "ok" : "degraded",
            Name: "tracer-net",
            Version: Version,
            UtcNow: DateTimeOffset.UtcNow,
            Database: new DatabaseHealth(databaseHealthy, stopwatch.Elapsed.TotalMilliseconds));

        return databaseHealthy
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }
}

public record HealthResponse(
    string Status,
    string Name,
    string Version,
    DateTimeOffset UtcNow,
    DatabaseHealth Database);

public record DatabaseHealth(bool Healthy, double DurationMs);
