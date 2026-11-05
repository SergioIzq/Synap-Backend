namespace Synap.Application.Features.Calendar;

/// <summary>
/// Comprueba unas credenciales CalDAV y localiza el `calendar-home-set` de la cuenta antes de
/// guardar nada (calendar-store-and-view design.md Decisión 2; specs/calendar-integration
/// "Conectar un calendario CalDAV"). La sincronización lee del home todos sus calendarios.
/// </summary>
public interface ICalDavDiscovery
{
    Task<CalDavDiscoveryResult> DiscoverAsync(
        Uri url, string username, string password, CancellationToken cancellationToken = default);
}

/// <summary>Los cuatro desenlaces que la spec distingue al conectar.</summary>
public abstract record CalDavDiscoveryResult
{
    private CalDavDiscoveryResult()
    {
    }

    /// <summary>
    /// La URL del home, con al menos un calendario de eventos. Si el servidor no deja llegar al
    /// home, es la del propio calendario, que se lista igual que un home de uno solo.
    /// </summary>
    public sealed record Found(string HomeUrl) : CalDavDiscoveryResult;

    public sealed record CredentialsRejected : CalDavDiscoveryResult;

    /// <summary><paramref name="Cause"/> es para los registros, nunca para el usuario.</summary>
    public sealed record Unreachable(string Cause) : CalDavDiscoveryResult;

    public sealed record NoCalendar : CalDavDiscoveryResult;
}
