using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Tracer.Api.Controllers;

/// <summary>
/// Liveness. The one endpoint that opts out of the fallback authorization
/// policy: a load balancer has no credential, and a probe that needs one stops
/// being a probe of the app and starts being a probe of the key store.
/// </summary>
[ApiController]
[Route("api/health")]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new HealthResponse("ok", "tracer-net"));
}

public record HealthResponse(string Status, string Service);
