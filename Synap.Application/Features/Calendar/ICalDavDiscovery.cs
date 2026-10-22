namespace Synap.Application.Features.Calendar;

/// <summary>
/// Comprueba unas credenciales CalDAV y localiza el calendario principal antes de guardar nada
/// (calendar-integration design.md Decisión 4; specs/calendar-integration "Conectar un calendario
/// CalDAV").
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

    /// <summary>La URL de la colección que se leerá a partir de ahora.</summary>
    public sealed record Found(string CalendarUrl) : CalDavDiscoveryResult;

    public sealed record CredentialsRejected : CalDavDiscoveryResult;

    /// <summary><paramref name="Cause"/> es para los registros, nunca para el usuario.</summary>
    public sealed record Unreachable(string Cause) : CalDavDiscoveryResult;

    public sealed record NoCalendar : CalDavDiscoveryResult;
}
