using System.Net;

namespace Synap.UnitTests.Services.Ai;

/// <summary>Intercepts outgoing HttpClient calls in tests, without a real server.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _responder;
    private readonly Exception? _exceptionToThrow;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    private FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>>? responder, Exception? exceptionToThrow)
    {
        _responder = responder;
        _exceptionToThrow = exceptionToThrow;
    }

    public static FakeHttpMessageHandler ReturningJson(HttpStatusCode statusCode, string json)
        => new(_ => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        }), null);

    public static FakeHttpMessageHandler Throwing(Exception exception) => new(null, exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        if (_exceptionToThrow is not null)
        {
            throw _exceptionToThrow;
        }

        return await _responder!(request);
    }
}
