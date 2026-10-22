using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Services.Calendar;
using System.Net;

namespace Synap.UnitTests.Services.Calendar;

/// <summary>
/// calendar-integration tarea 1.6: la URL CalDAV no puede apuntar a la red interna, y se rechaza
/// sin llegar a abrir un socket (design.md Decisión 5).
/// </summary>
public class CalDavNetworkGuardTests
{
    private int _socketsOpened;

    private CalDavCalendarClient ClientResolving(params IPAddress[] addresses)
    {
        var guard = new CalDavNetworkGuard(
            (_, _) => Task.FromResult(addresses),
            (_, _) =>
            {
                _socketsOpened++;
                throw new InvalidOperationException("no debería abrirse ningún socket");
            });

        return new CalDavCalendarClient(new HttpClient(guard.CreateHandler()), new RecordingLogger<CalDavCalendarClient>());
    }

    [Theory]
    [InlineData("https://127.0.0.1/dav/")]
    [InlineData("https://10.0.0.5/dav/")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/dav/")]
    public async Task Internal_addresses_are_refused_without_connecting(string url)
    {
        var result = await ClientResolving().DiscoverAsync(new Uri(url), "sergio", "secreto");

        Assert.IsType<CalDavDiscoveryResult.Unreachable>(result);
        Assert.Equal(0, _socketsOpened);
    }

    [Fact]
    public async Task A_host_that_resolves_to_a_private_address_is_refused_without_connecting()
    {
        var client = ClientResolving(IPAddress.Parse("192.168.1.20"));

        var read = await client.ReadAsync(
            new CalendarProviderRequest(
                Guid.NewGuid(), "https://calendario.example.com/dav/", new CalendarCredentials.CalDav("sergio", "secreto"),
                DateTime.UtcNow, DateTime.UtcNow.AddDays(1), 10),
            CancellationToken.None);

        Assert.IsType<CalendarReadResult.Unavailable>(read.Result);
        Assert.Equal(0, _socketsOpened);
    }

    [Fact]
    public async Task A_host_with_one_private_address_among_public_ones_is_refused()
    {
        var client = ClientResolving(IPAddress.Parse("203.0.113.10"), IPAddress.Parse("10.1.2.3"));

        var result = await client.DiscoverAsync(new Uri("https://calendario.example.com/"), "sergio", "secreto");

        Assert.IsType<CalDavDiscoveryResult.Unreachable>(result);
        Assert.Equal(0, _socketsOpened);
    }

    [Fact]
    public async Task Http_is_refused_without_connecting()
    {
        var result = await ClientResolving(IPAddress.Parse("203.0.113.10"))
            .DiscoverAsync(new Uri("http://calendario.example.com/"), "sergio", "secreto");

        Assert.IsType<CalDavDiscoveryResult.Unreachable>(result);
        Assert.Equal(0, _socketsOpened);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00:ec2::254", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("17.253.144.10", false)]
    [InlineData("2606:4700::1111", false)]
    public void Forbidden_ranges(string address, bool forbidden)
    {
        Assert.Equal(forbidden, CalDavNetworkGuard.IsForbidden(IPAddress.Parse(address)));
    }

    /// <summary>Una redirección a otro host no se sigue.</summary>
    [Fact]
    public async Task A_redirect_to_another_host_is_not_followed()
    {
        var requested = new List<Uri>();
        var client = new CalDavCalendarClient(
            new HttpClient(new RedirectingHandler(requested)), new RecordingLogger<CalDavCalendarClient>());

        var result = await client.DiscoverAsync(new Uri("https://calendario.example.com/dav/"), "sergio", "secreto");

        Assert.IsNotType<CalDavDiscoveryResult.Found>(result);
        Assert.DoesNotContain(requested, u => u.Host == "interno.example.net");
    }

    [Fact]
    public async Task An_oversized_response_is_unavailable()
    {
        var huge = new string('x', CalDavCalendarClient.MaxResponseBytes + 1);
        var client = new CalDavCalendarClient(
            new HttpClient(new FixedHandler((HttpStatusCode)207, huge)), new RecordingLogger<CalDavCalendarClient>());

        var read = await client.ReadAsync(
            new CalendarProviderRequest(
                Guid.NewGuid(), "https://calendario.example.com/dav/", new CalendarCredentials.CalDav("sergio", "secreto"),
                DateTime.UtcNow, DateTime.UtcNow.AddDays(1), 10),
            CancellationToken.None);

        Assert.IsType<CalendarReadResult.Unavailable>(read.Result);
    }

    private sealed class RedirectingHandler(List<Uri> requested) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            response.Headers.Location = new Uri("https://interno.example.net/dav/");
            return Task.FromResult(response);
        }
    }

    private sealed class FixedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
