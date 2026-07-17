using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Tracer.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Controllers report their own errors as ProblemDetails, but plenty of error
// responses never reach a controller: an unmatched route, a method that does not
// exist on a matched route, an unsupported content type, an unhandled exception.
// Registering the service and the two middlewares below means those come back as
// RFC 7807 as well, rather than as an empty body the client has to guess at.
builder.Services.AddProblemDetails();

builder.Services.AddDbContext<TracerDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Tracer") ?? "Data Source=tracer.db"));

var app = builder.Build();

app.UseExceptionHandler(); // unhandled exception -> 500 ProblemDetails
app.UseStatusCodePages(); // error status with no body -> ProblemDetails

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TracerDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(db);
}

app.MapControllers();

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
