using Synap.Domain;

namespace Synap.Application.Features.Calendar;

/// <summary>
/// El bloque "calendar" de la configuración del usuario (specs/calendar-integration "Las
/// credenciales del calendario nunca se revelan"): si el despliegue ofrece Google y el estado de la
/// conexión, sin ninguna credencial. Se construye desde una proyección que no las lee.
/// </summary>
public sealed record CalendarSettingsResponse(bool GoogleCalendarAvailable, CalendarConnectionStatus? Connection)
{
    public static CalendarSettingsResponse From(CalendarConnectionSummary? summary, bool googleAvailable) =>
        new(googleAvailable, summary is null ? null : CalendarConnectionStatus.From(summary));
}

/// <summary>Proveedor, cuenta, desde cuándo y si necesita reconectarse.</summary>
public sealed record CalendarConnectionStatus(
    string Provider,
    string Account,
    DateTime ConnectedAt,
    bool NeedsReconnect,
    DateTime? NeedsReconnectSince)
{
    public static CalendarConnectionStatus From(CalendarConnectionSummary summary) => new(
        summary.Provider == CalendarProvider.Google ? "google" : "caldav",
        summary.AccountLabel,
        summary.ConnectedAt,
        summary.NeedsReconnectSince is not null,
        summary.NeedsReconnectSince);
}
