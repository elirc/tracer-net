using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tracer.Api.Auth;
using Tracer.Api.Webhooks;
using Tracer.Infrastructure;

namespace Tracer.Tests.Integration;

/// <summary>
/// WebApplicationFactory that swaps the app database for a shared in-memory
/// SQLite connection. The connection is held open for the factory's lifetime
/// so the schema and seed data survive across requests.
///
/// <para>
/// Clients come pre-credentialed from the seeded dev keys. Note there is no
/// test-only authentication stub: the tests go through the real
/// <see cref="ApiKeyAuthenticationHandler"/>, hashing and all. Swapping in a
/// fake handler would make every authorization test a test of the fake — the
/// denial matrix would keep passing even if key lookup were broken outright.
/// </para>
/// </summary>
public class TracerApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    /// <summary>
    /// Stands in for every webhook subscriber. Tests script its responses and
    /// read back what was sent.
    /// </summary>
    public StubWebhookEndpoint WebhookEndpoint { get; } = new();

    /// <summary>
    /// Drains the webhook outbox once, synchronously, and reports how many
    /// deliveries were attempted.
    ///
    /// The background worker is removed below and this is called explicitly
    /// instead. A test that has to wait for a five-second poll is a test that is
    /// either slow or flaky, and usually manages both — while the thing worth
    /// asserting is what a drain *does*, not that a timer eventually fires.
    /// </summary>
    public async Task<int> DrainWebhooksAsync()
    {
        using var scope = Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<WebhookSender>();
        return await sender.DeliverDueAsync();
    }

    /// <summary>Workspace admin "ana": reaches every team.</summary>
    public HttpClient CreateAdminClient() => CreateClientWithKey(DevApiKeys.Admin);

    /// <summary>Member "ben": on Engineering only.</summary>
    public HttpClient CreateEngMemberClient() => CreateClientWithKey(DevApiKeys.EngMember);

    /// <summary>Member "dana": on Design only — foreign to everything Engineering owns.</summary>
    public HttpClient CreateDesMemberClient() => CreateClientWithKey(DevApiKeys.DesMember);

    /// <summary>No credential at all.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient();

    public HttpClient CreateClientWithKey(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, apiKey);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<TracerDbContext>));
            services.AddDbContext<TracerDbContext>(options => options.UseSqlite(_connection));

            // Out with the polling worker: tests drive delivery through
            // DrainWebhooksAsync so that "did it send?" is a question with an
            // answer rather than a race against a timer.
            services.RemoveAll(typeof(IHostedService));

            // Every webhook request goes to the stub instead of the network.
            services.AddHttpClient(WebhookSender.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => WebhookEndpoint);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}

file static class ServiceCollectionExtensions
{
    public static void RemoveAll(this IServiceCollection services, Type serviceType)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == serviceType)
            {
                services.RemoveAt(i);
            }
        }
    }
}
