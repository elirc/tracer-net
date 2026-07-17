using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Tracer.Tests.Integration;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_returns_200()
    {
        var response = await _client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_reports_ok_status_and_service_name()
    {
        var body = await _client.GetFromJsonAsync<HealthPayload>("/api/health");

        Assert.NotNull(body);
        Assert.Equal("ok", body.Status);
        Assert.Equal("tracer-net", body.Service);
    }

    private sealed record HealthPayload(string Status, string Service);
}
