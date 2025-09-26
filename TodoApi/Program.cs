using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using TodoApi.Integration;
using TodoApi.Integration.Fakes;
using TodoApi.Options;
using TodoApi.Sync;

var builder = WebApplication.CreateBuilder(args);

// Services registration MUST happen before Build()
builder.Services
    .AddDbContext<TodoContext>(opt =>
        opt.UseSqlServer(builder.Configuration.GetConnectionString("TodoContext")))
    .AddEndpointsApiExplorer()
    .AddControllers();

// Logging configuration (before Build)
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Bind ExternalApi options (before Build)
builder.Services.Configure<ExternalApiOptions>(builder.Configuration.GetSection("ExternalApi"));

// Choose real or fake client based on options (before Build)
var useFake = builder.Configuration.GetValue<bool>("ExternalApi:UseFake");
if (useFake)
{
    // Fake client keeps in-memory state during app lifetime
    builder.Services.AddSingleton<IExternalTodoApiClient, FakeExternalTodoApiClient>();
}
else
{
    // Real HTTP client
    builder.Services.AddHttpClient<IExternalTodoApiClient, ExternalTodoApiClient>((sp, http) =>
    {
        var opts = sp.GetRequiredService<IOptions<ExternalApiOptions>>().Value;
        http.BaseAddress = new Uri(opts.BaseUrl);
        http.Timeout = TimeSpan.FromSeconds(Math.Max(5, opts.TimeoutSeconds));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    });
}

// Register hosted sync service (before Build)
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
