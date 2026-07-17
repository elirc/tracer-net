using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tracer.Api.Auth;
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
