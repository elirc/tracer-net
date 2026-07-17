using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

public class HealthEndpointTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(TracerApiFactory factory)
    {
        _client = factory.CreateAnonymousClient();
    }

    [Fact]
    public async Task Health_returns_200_when_database_is_reachable()
    {
        var response = await _client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_reports_ok_status_name_and_version()
    {
        var body = await _client.GetFromJsonAsync<HealthPayload>("/api/health");

        Assert.NotNull(body);
        Assert.Equal("ok", body.Status);
        Assert.Equal("tracer-net", body.Name);
        Assert.False(string.IsNullOrWhiteSpace(body.Version));
    }

    [Fact]
    public async Task Health_probes_the_database_and_reports_it_healthy()
    {
        var body = await _client.GetFromJsonAsync<HealthPayload>("/api/health");

        Assert.NotNull(body);
        Assert.NotNull(body.Database);
        Assert.True(body.Database!.Healthy);
        Assert.True(body.Database.DurationMs >= 0);
        Assert.True(body.UtcNow > DateTimeOffset.UnixEpoch);
    }

    private sealed record HealthPayload(
        string Status,
        string Name,
        string Version,
        DateTimeOffset UtcNow,
        DatabasePayload? Database);

    private sealed record DatabasePayload(bool Healthy, double DurationMs);
}
