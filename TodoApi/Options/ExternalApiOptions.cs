namespace TodoApi.Options;

// Options bound from configuration.
public sealed class ExternalApiOptions
{
    public string BaseUrl { get; set; } = "http://localhost";
    public int SyncPeriodSeconds { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 15;

    // When true, the app registers a fake IExternalTodoApiClient instead of the real HTTP client.
    public bool UseFake { get; set; } = false;
}