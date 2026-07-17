using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tracer.Domain;
using Tracer.Infrastructure;

namespace Tracer.Tests.Integration;

/// <summary>
/// Who the API thinks you are, and what it does when it cannot tell.
/// </summary>
public class AuthenticationApiTests : IClassFixture<TracerApiFactory>
{
    private readonly TracerApiFactory _factory;

    public AuthenticationApiTests(TracerApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record MePayload(Guid Id, string Handle, string Name, string Role, List<TeamPayload> Teams);
    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record CreatedKeyPayload(Guid Id, Guid UserId, string Name, string Prefix, string Token);
    private sealed record KeyPayload(Guid Id, string Name, string Prefix, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);
    private sealed record UserPayload(Guid Id, string Handle, string Name, string Role);

    [Fact]
    public async Task No_credential_is_rejected_with_401()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/api/teams");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_key_is_rejected_with_401()
    {
        var response = await _factory.CreateClientWithKey("trk_not_a_real_key").GetAsync("/api/teams");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_key_header_is_rejected_with_401()
    {
        var response = await _factory.CreateClientWithKey("").GetAsync("/api/teams");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The 401 an authentication challenge produces has no body of its own; it
    /// only becomes RFC 7807 because the auth middleware sits inside
    /// UseStatusCodePages. Reorder those two and this is what breaks.
    /// </summary>
    [Fact]
    public async Task An_unauthorized_response_is_still_a_problem_document()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/api/teams");

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Health_needs_no_credential()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Me_reports_the_admin_and_every_team()
    {
        var me = await _factory.CreateAdminClient().GetFromJsonAsync<MePayload>("/api/me");

        Assert.NotNull(me);
        Assert.Equal("ana", me.Handle);
        Assert.Equal("Admin", me.Role);
        Assert.Contains(me.Teams, t => t.Key == "ENG");
        Assert.Contains(me.Teams, t => t.Key == "DES");
    }

    [Fact]
    public async Task Me_reports_a_member_and_only_their_teams()
    {
        var me = await _factory.CreateDesMemberClient().GetFromJsonAsync<MePayload>("/api/me");

        Assert.NotNull(me);
        Assert.Equal("dana", me.Handle);
        Assert.Equal("Member", me.Role);
        Assert.Equal(["DES"], me.Teams.Select(t => t.Key).ToArray());
    }

    [Fact]
    public async Task A_minted_key_authenticates_and_its_token_is_never_returned_again()
    {
        var admin = _factory.CreateAdminClient();
        var created = await admin.PostAsJsonAsync("/api/users", new { handle = "mira", name = "Mira Vale" });
        var user = await created.Content.ReadFromJsonAsync<UserPayload>();

        var minted = await admin.PostAsJsonAsync($"/api/users/{user!.Id}/api-keys", new { name = "laptop" });
        Assert.Equal(HttpStatusCode.Created, minted.StatusCode);
        var key = await minted.Content.ReadFromJsonAsync<CreatedKeyPayload>();
        Assert.StartsWith("trk_", key!.Token);

        // The token works.
        var me = await _factory.CreateClientWithKey(key.Token).GetFromJsonAsync<MePayload>("/api/me");
        Assert.Equal("mira", me!.Handle);

        // And is never handed back — only the prefix identifies it afterwards.
        var listed = await admin.GetListAsync<KeyPayload>($"/api/users/{user.Id}/api-keys");
        var stored = Assert.Single(listed!);
        Assert.Equal(key.Prefix, stored.Prefix);
        var raw = await (await admin.GetAsync($"/api/api-keys/{key.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(key.Token, raw);
    }

    [Fact]
    public async Task Using_a_key_records_that_it_was_used()
    {
        var admin = _factory.CreateAdminClient();
        var created = await admin.PostAsJsonAsync("/api/users", new { handle = "seen", name = "Seen User" });
        var user = await created.Content.ReadFromJsonAsync<UserPayload>();
        var minted = await admin.PostAsJsonAsync($"/api/users/{user!.Id}/api-keys", new { name = "cli" });
        var key = await minted.Content.ReadFromJsonAsync<CreatedKeyPayload>();

        var before = await admin.GetFromJsonAsync<KeyPayload>($"/api/api-keys/{key!.Id}");
        Assert.Null(before!.LastUsedAt);

        await _factory.CreateClientWithKey(key.Token).GetAsync("/api/me");

        var after = await admin.GetFromJsonAsync<KeyPayload>($"/api/api-keys/{key.Id}");
        Assert.NotNull(after!.LastUsedAt);
    }

    [Fact]
    public async Task A_revoked_key_stops_authenticating()
    {
        var admin = _factory.CreateAdminClient();
        var created = await admin.PostAsJsonAsync("/api/users", new { handle = "rev", name = "Rev Oked" });
        var user = await created.Content.ReadFromJsonAsync<UserPayload>();
        var minted = await admin.PostAsJsonAsync($"/api/users/{user!.Id}/api-keys", new { name = "leaked" });
        var key = await minted.Content.ReadFromJsonAsync<CreatedKeyPayload>();
        var client = _factory.CreateClientWithKey(key!.Token);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);

        var revoked = await admin.DeleteAsync($"/api/api-keys/{key.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);

        // The row survives revocation, so the audit trail still knows the key existed.
        var stored = await admin.GetFromJsonAsync<KeyPayload>($"/api/api-keys/{key.Id}");
        Assert.NotNull(stored!.RevokedAt);
    }

    [Fact]
    public async Task Revoking_is_idempotent()
    {
        var admin = _factory.CreateAdminClient();
        var created = await admin.PostAsJsonAsync("/api/users", new { handle = "twice", name = "Two Times" });
        var user = await created.Content.ReadFromJsonAsync<UserPayload>();
        var minted = await admin.PostAsJsonAsync($"/api/users/{user!.Id}/api-keys", new { name = "k" });
        var key = await minted.Content.ReadFromJsonAsync<CreatedKeyPayload>();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/api-keys/{key!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/api-keys/{key.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_member_manages_their_own_keys_but_not_another_users()
    {
        var admin = _factory.CreateAdminClient();
        var users = (await admin.GetListAsync<UserPayload>("/api/users"))!;
        var ben = users.Single(u => u.Handle == "ben");
        var dana = users.Single(u => u.Handle == "dana");
        var benClient = _factory.CreateEngMemberClient();

        var own = await benClient.PostAsJsonAsync($"/api/users/{ben.Id}/api-keys", new { name = "ben's second" });
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);

        var other = await benClient.PostAsJsonAsync($"/api/users/{dana.Id}/api-keys", new { name = "not mine" });
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);

        var otherList = await benClient.GetAsync($"/api/users/{dana.Id}/api-keys");
        Assert.Equal(HttpStatusCode.Forbidden, otherList.StatusCode);
    }

    /// <summary>
    /// Reaching another user's key by its own id is a 404, not a 403: a 403 there
    /// would only ever be reachable for an id that exists, which turns the status
    /// code into an oracle for guessing valid key ids.
    /// </summary>
    [Fact]
    public async Task Another_users_key_is_not_found_rather_than_forbidden()
    {
        var admin = _factory.CreateAdminClient();
        var users = (await admin.GetListAsync<UserPayload>("/api/users"))!;
        var dana = users.Single(u => u.Handle == "dana");
        var minted = await admin.PostAsJsonAsync($"/api/users/{dana.Id}/api-keys", new { name = "dana's" });
        var key = await minted.Content.ReadFromJsonAsync<CreatedKeyPayload>();

        var response = await _factory.CreateEngMemberClient().GetAsync($"/api/api-keys/{key!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Keys_are_stored_hashed_never_in_the_clear()
    {
        var admin = _factory.CreateAdminClient();
        var created = await admin.PostAsJsonAsync("/api/users", new { handle = "hashed", name = "Hash Ed" });
        var user = await created.Content.ReadFromJsonAsync<UserPayload>();
        var minted = await admin.PostAsJsonAsync($"/api/users/{user!.Id}/api-keys", new { name = "k" });
        var key = await minted.Content.ReadFromJsonAsync<CreatedKeyPayload>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TracerDbContext>();
        var stored = db.ApiKeys.Single(k => k.Id == key!.Id);

        Assert.NotEqual(key!.Token, stored.KeyHash);
        Assert.Equal(ApiKeyToken.Hash(key.Token), stored.KeyHash);
    }
}
