var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
