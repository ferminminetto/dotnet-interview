using System.Net;
using System.Net.Http;

namespace TodoApi.Tests.TestUtils;

/* 
 * Minimal programmable HttpMessageHandler for unit tests.
 * Using this we can mock HttpClient calls.
 */
public sealed class TestHttpMessageHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();

    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

    public TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        => _handler = handler;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var response = _handler(request, cancellationToken);
        return Task.FromResult(response);
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json)
        => new(code)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
}