using Synap.Application.Features.Calendar;
using System.Collections.Concurrent;

namespace Synap.IntegrationTests;

/// <summary>
/// El descubrimiento CalDAV tal como lo ve la API en pruebas. Como el fixture es compartido, el
/// resultado depende de la contraseña: "rechazada", "caida" y "vacia" dan cada uno de los fallos
/// de la spec; cualquier otra encuentra `&lt;url&gt;calendars/default/`.
/// </summary>
public sealed class ApiCalDavDiscoveryFake : ICalDavDiscovery
{
    public ConcurrentBag<Uri> Contacted { get; } = [];

    public Task<CalDavDiscoveryResult> DiscoverAsync(
        Uri url, string username, string password, CancellationToken cancellationToken = default)
    {
        Contacted.Add(url);
        CalDavDiscoveryResult result = password switch
        {
            "rechazada" => new CalDavDiscoveryResult.CredentialsRejected(),
            "caida" => new CalDavDiscoveryResult.Unreachable("en un test"),
            "vacia" => new CalDavDiscoveryResult.NoCalendar(),
            _ => new CalDavDiscoveryResult.Found(new Uri(url, "calendars/default/").AbsoluteUri),
        };
        return Task.FromResult(result);
    }
}
