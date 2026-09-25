using Synap.Api;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// backend-hardening tasks 3.1/3.2 - specs/identity "Authentication attempt limiting", keyed by
/// the client IP that UseForwardedHeaders resolves. Each test uses its own IPs so buckets never
/// overlap with other tests sharing the host.
/// </summary>
[Collection(ApiCollection.Name)]
public class AuthRateLimitTests
{
    private readonly ApiFixture _api;

    public AuthRateLimitTests(ApiFixture api) => _api = api;

    private static Task<HttpResponseMessage> AttemptLoginAsync(HttpClient client, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = "nadie@example.com", password = "incorrecta" }),
        };
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Eleventh_login_attempt_within_a_minute_is_rejected_with_a_spanish_message()
    {
        var client = _api.CreateClient("10.1.0.1");

        for (var i = 0; i < RateLimitPolicies.AuthPermitLimit; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await AttemptLoginAsync(client)).StatusCode);
        }

        var rejected = await AttemptLoginAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(RateLimitPolicies.RejectionMessage, await rejected.ErrorMessageAsync());

        // A different client is unaffected.
        Assert.Equal(HttpStatusCode.Unauthorized, (await AttemptLoginAsync(_api.CreateClient("10.1.0.2"))).StatusCode);
    }

    [Fact]
    public async Task Forwarded_client_ip_is_honoured_from_the_known_proxy()
    {
        var viaProxy = _api.CreateClient(ApiFixture.KnownProxyIp);

        for (var i = 0; i < RateLimitPolicies.AuthPermitLimit; i++)
        {
            await AttemptLoginAsync(viaProxy, "203.0.113.10");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await AttemptLoginAsync(viaProxy, "203.0.113.10")).StatusCode);
        // Another real client behind the same proxy has its own bucket.
        Assert.Equal(HttpStatusCode.Unauthorized, (await AttemptLoginAsync(viaProxy, "203.0.113.11")).StatusCode);
    }

    [Fact]
    public async Task Forwarded_header_from_an_unknown_address_cannot_dodge_the_limit()
    {
        var spoofing = _api.CreateClient("10.1.0.3");

        for (var i = 0; i < RateLimitPolicies.AuthPermitLimit; i++)
        {
            await AttemptLoginAsync(spoofing, $"198.51.100.{i}");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await AttemptLoginAsync(spoofing, "198.51.100.200")).StatusCode);
    }

    [Fact]
    public async Task Sixth_registration_within_an_hour_is_rejected()
    {
        var client = _api.CreateClient("10.1.0.4");

        for (var i = 0; i < RateLimitPolicies.RegisterPermitLimit; i++)
        {
            (await client.PostAsJsonAsync("/api/auth/register", new { email = $"{Guid.NewGuid():N}@example.com", password = "Passw0rd!123" }))
                .EnsureSuccessStatusCode();
        }

        var rejected = await client.PostAsJsonAsync("/api/auth/register", new { email = $"{Guid.NewGuid():N}@example.com", password = "Passw0rd!123" });

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }
}
