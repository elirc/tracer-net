using Microsoft.AspNetCore.Mvc;

namespace Tracer.Api.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new HealthResponse("ok", "tracer-net"));
}

public record HealthResponse(string Status, string Service);
