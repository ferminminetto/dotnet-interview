using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using TodoApi.Integration;
using TodoApi.Integration.Fakes;
using TodoApi.Options;
using TodoApi.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddDbContext<TodoContext>(opt =>
        opt.UseSqlServer(builder.Configuration.GetConnectionString("TodoContext")))
    .AddEndpointsApiExplorer()
    .AddControllers();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.Configure<ExternalApiOptions>(builder.Configuration.GetSection("ExternalApi"));

// Choose real or fake client based on options
var useFake = builder.Configuration.GetValue<bool>("ExternalApi:UseFake");
if (useFake)
{
    // Fake client keeps in-memory state during app lifetime
    builder.Services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();
}
else
{
    builder.Services.AddExternalTodoApiClient();
}

// Register sync service
builder.Services.AddHostedService<ExternalTodoApiSyncService>();

var app = builder.Build();

// Apply pending migrations at startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TodoContext>();
    db.Database.Migrate();
}

app.UseAuthorization();
app.MapControllers();
app.Run();
