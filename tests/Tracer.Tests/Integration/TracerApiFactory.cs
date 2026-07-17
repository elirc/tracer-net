using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tracer.Infrastructure;

namespace Tracer.Tests.Integration;

/// <summary>
/// WebApplicationFactory that swaps the app database for a shared in-memory
/// SQLite connection. The connection is held open for the factory's lifetime
/// so the schema and seed data survive across requests.
/// </summary>
public class TracerApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

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
