using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using TodoApi.Integration;
using TodoApi.Options;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddExternalTodoApiClient(this IServiceCollection services)
    {
        /*
         * Adding an HTTP client for IExternalTodoApiClient with resilience policies.
         * We are applying: timeouts of 10s, retries up to 3 times with exponential backoff and circuit breaker (default configuration).
         */
        services.AddHttpClient<IExternalTodoApiClient, ExternalTodoApiClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<ExternalApiOptions>>().Value;
            http.BaseAddress = new Uri(opts.BaseUrl);
            http.Timeout = Timeout.InfiniteTimeSpan;
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("todoapi-client/1.0");
        })
        .AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.Retry.MaxRetryAttempts = 3;
        });

        return services;
    }
}
