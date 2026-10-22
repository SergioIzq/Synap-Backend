namespace Synap.Application.Features.Calendar;

/// <summary>
/// El cliente OAuth de Google del despliegue, de la sección "GoogleCalendar"
/// (calendar-integration design.md Decisión 3). Vacío es un estado válido: sin
/// <see cref="ClientId"/> solo se ofrece CalDAV. El secreto nunca se comitea.
/// </summary>
public sealed class GoogleCalendarOptions
{
    public const string SectionName = "GoogleCalendar";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Adonde vuelve el usuario desde Google: `&lt;api&gt;/api/calendar/google/callback`.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(ClientId);
}
