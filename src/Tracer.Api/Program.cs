using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Middleware;
using Tracer.Api.Notifications;
using Tracer.Api.RateLimiting;
using Tracer.Api.Webhooks;
using Tracer.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(options =>
{
    // Fail closed. Without a fallback policy, authorization is opt-in per
    // controller, so the first endpoint someone adds without remembering
    // [Authorize] is silently public — and nothing fails to tell them. With it,
    // forgetting the attribute denies the request instead, and opening an
    // endpoint up is a deliberate [AllowAnonymous] that shows up in review.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Write-heavy endpoints draw from a per-credential fixed window; reads are never
// throttled. See WriteRateLimiter for why only mutating methods are metered.
builder.Services.AddWriteRateLimiter();

builder.Services.AddScoped<TeamAccess>();
builder.Services.AddScoped<ActivityRecorder>();
builder.Services.AddScoped<NotificationFanout>();
builder.Services.AddScoped<WebhookOutbox>();
builder.Services.AddScoped<WebhookSender>();

// A named client so webhook calls are configured as what they are: requests to
// someone else's server, which may be slow, hostile, or a black hole. The
// timeout is the important part — the default is 100 seconds, which is long
// enough for one dead endpoint to occupy the delivery worker indefinitely.
builder.Services.AddHttpClient(WebhookSender.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // A signed payload must not be quietly forwarded somewhere else because a
    // redirect said so; the signature travels in a header and the destination is
    // what the team registered. A 3xx is a misconfigured endpoint, and
    // WebhookRetryPolicy classifies it as such.
    AllowAutoRedirect = false,
});

builder.Services.AddHostedService<WebhookDeliveryWorker>();

// Controllers report their own errors as ProblemDetails, but plenty of error
// responses never reach a controller: an unmatched route, a method that does not
// exist on a matched route, an unsupported content type, an unhandled exception.
// Registering the service and the two middlewares below means those come back as
// RFC 7807 as well, rather than as an empty body the client has to guess at.
builder.Services.AddProblemDetails();

builder.Services.AddDbContext<TracerDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Tracer") ?? "Data Source=tracer.db"));

var app = builder.Build();

// Outermost, so its elapsed time covers the whole pipeline — auth, binding, the
// handler, and the exception middleware below — and its log line still runs when
// a request throws.
app.UseRequestLogging();

app.UseExceptionHandler(); // unhandled exception -> 500 ProblemDetails
app.UseStatusCodePages(); // error status with no body -> ProblemDetails

// Metered before authentication: the partition key is the raw API-key header, and
// throttling an abusive caller should not first pay for a key lookup on every one
// of their requests.
app.UseRateLimiter();

// Both sit inside UseStatusCodePages so that the bodyless 401 an authentication
// challenge produces, and the bodyless 403 a role check produces, come back as
// ProblemDetails like every other error rather than as an empty response.
app.UseAuthentication();
app.UseAuthorization();

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
