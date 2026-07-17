using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// Every failure leaves the API as RFC 7807, whichever layer produced it —
/// a controller, model validation, or routing before a controller ever ran.
/// </summary>
public class ProblemDetailsTests : IClassFixture<TracerApiFactory>
{
    private readonly HttpClient _client;

    public ProblemDetailsTests(TracerApiFactory factory)
    {
        _client = factory.CreateAdminClient();
    }

    private sealed record TeamPayload(Guid Id, string Key);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task<TeamPayload> GetTeamAsync(string key)
    {
        var teams = await _client.GetListAsync<TeamPayload>("/api/teams");
        return teams!.Single(t => t.Key == key);
    }

    [Fact]
    public async Task Not_found_returns_a_problem_document_naming_the_resource()
    {
        var missing = Guid.NewGuid();

        var response = await _client.GetAsync($"/api/issues/{missing}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("Issue not found.", problem.GetProperty("title").GetString());
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Contains(missing.ToString(), problem.GetProperty("detail").GetString());
        // Filled in by ProblemDetailsFactory; hand-built ProblemDetails lack both.
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("type").GetString()));
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Theory]
    [InlineData("teams")]
    [InlineData("projects")]
    [InlineData("issues")]
    [InlineData("cycles")]
    [InlineData("states")]
    [InlineData("labels")]
    [InlineData("comments")]
    public async Task Every_resource_reports_a_missing_id_the_same_way(string resource)
    {
        var response = await _client.GetAsync($"/api/{resource}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.EndsWith("not found.", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Conflicts_return_409_as_a_problem_document()
    {
        await _client.PostAsJsonAsync("/api/teams", new { name = "Dup Guard", key = "DUPG" });

        var response = await _client.PostAsJsonAsync("/api/teams", new { name = "Dup Guard Again", key = "DUPG" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("Team key already in use.", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Domain_rule_violations_return_422_as_a_problem_document()
    {
        var team = await GetTeamAsync("ENG");
        var now = DateTimeOffset.UtcNow;

        var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/cycles",
            new { name = "backwards", startsAt = now.AddDays(5), endsAt = now });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(422, problem.GetProperty("status").GetInt32());
        Assert.Equal("Invalid cycle dates.", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Model_validation_failures_return_400_with_per_field_errors()
    {
        var team = await GetTeamAsync("ENG");

        var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/issues",
            new { description = "no title", estimate = 5000 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Title", out _));
        Assert.True(errors.TryGetProperty("Estimate", out _)); // outside [Range(0, 100)]
    }

    [Fact]
    public async Task A_missing_required_date_is_reported_as_a_bad_request_not_a_domain_rule()
    {
        var team = await GetTeamAsync("ENG");

        // Without [Required] on a nullable date this would bind to 0001-01-01 and
        // surface as "a cycle must end after it starts" (422), blaming the domain
        // for what is really a malformed request.
        var response = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/cycles", new { name = "dateless" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.True(problem.GetProperty("errors").TryGetProperty("StartsAt", out _));
    }

    [Fact]
    public async Task A_missing_transition_target_is_reported_as_a_bad_request()
    {
        var team = await GetTeamAsync("ENG");
        var created = await _client.PostAsJsonAsync($"/api/teams/{team.Id}/issues", new { title = "needs a target" });
        var issue = await created.Content.ReadFromJsonAsync<JsonElement>();

        var response = await _client.PostAsJsonAsync(
            $"/api/issues/{issue.GetProperty("id").GetGuid()}/transitions", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.True(problem.GetProperty("errors").TryGetProperty("StateId", out _));
    }

    [Fact]
    public async Task Unmatched_routes_return_a_problem_document_rather_than_an_empty_body()
    {
        // Produced by routing, never reaching a controller: only the status code
        // pages middleware gives this a body.
        var response = await _client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Wrong_method_on_a_real_route_returns_405_as_a_problem_document()
    {
        var response = await _client.PutAsJsonAsync("/api/teams", new { name = "nope", key = "NOPE" });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(405, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Malformed_json_returns_400_as_a_problem_document()
    {
        var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/teams", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ProblemAsync(response);
    }

    [Fact]
    public async Task Unsupported_content_type_returns_415_as_a_problem_document()
    {
        var content = new StringContent("name=x&key=X", Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await _client.PostAsync("/api/teams", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(415, problem.GetProperty("status").GetInt32());
    }
}
