using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synap.Shared.Application.Interfaces;
using Npgsql;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Testcontainers.PostgreSql;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// The real API (Program.cs: auth schemes, rate limiter, forwarded headers, MediatR, EF) over
/// its own pgvector container - for behaviour that only exists in the HTTP pipeline. One host
/// per test run: Program's Serilog bootstrap logger can only be frozen once per process.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string KnownProxyIp = "10.0.0.1";

    /// <summary>Tests set the TCP peer address with this header (TestServer has none).</summary>
    public const string RemoteIpHeader = "X-Test-Remote-Ip";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("synap_api_test")
        .WithUsername("synap_test")
        .WithPassword("synap_test")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Every email the API "sends" during the test run - nothing leaves the process.</summary>
    public CapturingEmailSender Emails { get; } = new();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
            builder.UseSetting("AiService:BaseUrl", "http://ai-service.invalid");
            builder.UseSetting("ForwardedHeaders:KnownProxies", KnownProxyIp);
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(Emails);
            });
        });

        // Forces host start-up (and with it Program's Database.Migrate()).
        _factory.CreateClient().Dispose();
    }

    private int _nextClientIp;

    /// <summary>
    /// Each client gets its own IP unless one is given, so the per-IP auth rate limits of one
    /// test never spill into another.
    /// </summary>
    public HttpClient CreateClient(string? remoteIp = null)
    {
        var client = _factory.CreateClient();
        var n = Interlocked.Increment(ref _nextClientIp);
        client.DefaultRequestHeaders.Add(RemoteIpHeader, remoteIp ?? $"10.200.{n / 250}.{n % 250 + 1}");
        return client;
    }

    /// <summary>Signs arbitrary claims with the Development JWT settings the test host uses.</summary>
    public static string SignToken(IEnumerable<System.Security.Claims.Claim> claims)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json"))
            .Build();
        var settings = configuration.GetSection("JwtSettings");
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(settings["SecretKey"]!));
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            settings["Issuer"], settings["Audience"], claims, DateTime.UtcNow, DateTime.UtcNow.AddHours(1),
            new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Runs before Program's pipeline, i.e. before UseForwardedHeaders.</summary>
    private sealed class FakeRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, RequestDelegate nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteIpHeader, out var ip) && IPAddress.TryParse(ip, out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "Api";
}

/// <summary>Small helpers over the API's Result JSON shape.</summary>
public static class ApiClientExtensions
{
    public const string Password = "Passw0rd!123";

    public static async Task<(string Email, string Token)> RegisterAndLoginAsync(this HttpClient client, string? email = null)
    {
        email ??= $"{Guid.NewGuid():N}@example.com";

        var register = await client.PostAsJsonAsync("/api/auth/register", new { email, password = Password });
        register.EnsureSuccessStatusCode();

        return (email, await client.LoginAsync(email, Password));
    }

    public static async Task<string> LoginAsync(this HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync()).GetProperty("value").GetProperty("token").GetString()!;
    }

    public static HttpClient WithBearer(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static async Task<string?> ErrorMessageAsync(this HttpResponseMessage response)
        => (await response.ReadJsonAsync()).GetProperty("error").GetProperty("message").GetString();

    public static Task<HttpResponseMessage> DeleteAsJsonAsync<T>(this HttpClient client, string url, T body)
        => client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, url) { Content = JsonContent.Create(body) });
}

public sealed class CapturingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];

    public void Enqueue(EmailMessage message)
    {
        lock (_sent) _sent.Add(message);
    }

    public IReadOnlyList<EmailMessage> To(string email)
    {
        lock (_sent) return _sent.Where(m => m.To == email).ToList();
    }

    /// <summary>The token from the reset link in the latest email sent to this address.</summary>
    public string LatestResetToken(string email)
    {
        var text = To(email).Last().TextBody;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"reset-password\?token=([^\s]+)");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }
}
