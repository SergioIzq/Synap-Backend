namespace Synap.Application.Features.Calendar;

/// <summary>
/// El cliente OAuth de Google del despliegue (calendar-integration design.md Decisión 3). Sin
/// <see cref="ClientId"/>, Google no se ofrece y solo queda CalDAV.
/// </summary>
public sealed class GoogleCalendarOptions
{
    public const string SectionName = "GoogleCalendar";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>La URI registrada en Google: `&lt;api&gt;/api/calendar/google/callback`.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RedirectUri);
}
