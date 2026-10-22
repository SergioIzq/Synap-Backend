using System.Text.RegularExpressions;

namespace Synap.Domain;

/// <summary>
/// La antelación respecto a un evento tal como la dice el usuario ("una hora antes", "media hora
/// antes", "30 minutos antes", "el día antes", "dos días antes") convertida en un instante UTC a
/// partir del inicio del evento (calendar-integration design.md Decisión 7). Una antelación en
/// días no tiene hora: es ese día a las <see cref="ReminderWording.DefaultTimeOfDay"/> locales.
///
/// Igual de estrecha que <see cref="ReminderWording"/>: lo que no entiende devuelve null, para
/// preguntar en vez de adivinar.
/// </summary>
public static class ReminderOffsetWording
{
    public static DateTime? Resolve(string? wording, CalendarEvent calendarEvent, string? timezone)
    {
        if (string.IsNullOrWhiteSpace(wording))
        {
            return null;
        }

        var text = ReminderWording.Normalize(wording);
        text = Regex.Replace(text, @"\s+(de|del)\s+(la|el)?\s*(evento|reunion|cita)?$", string.Empty).Trim();

        // El inicio del evento en hora local; uno de todo el día empieza a medianoche de su día.
        var localStart = calendarEvent.AllDayDate is { } day
            ? day.ToDateTime(TimeOnly.MinValue)
            : UserClock.ToLocal(calendarEvent.StartUtc, timezone);

        if (text is "dia antes" or "el dia antes" or "un dia antes" or "la vispera")
        {
            return AtMorning(localStart.Date.AddDays(-1), timezone);
        }

        if (text is "media hora antes")
        {
            return Before(calendarEvent, localStart, timezone, TimeSpan.FromMinutes(30));
        }

        if (text is "un cuarto de hora antes" or "cuarto de hora antes")
        {
            return Before(calendarEvent, localStart, timezone, TimeSpan.FromMinutes(15));
        }

        var match = Regex.Match(text, @"^(?<amount>\d+|[a-z]+)\s+(?<unit>minuto|min|hora|dia|semana)s?\s+antes$");
        if (!match.Success || ReminderWording.Amount(match.Groups["amount"].Value) is not { } amount || amount <= 0)
        {
            return null;
        }

        return match.Groups["unit"].Value switch
        {
            "minuto" or "min" => Before(calendarEvent, localStart, timezone, TimeSpan.FromMinutes(amount)),
            "hora" => Before(calendarEvent, localStart, timezone, TimeSpan.FromHours(amount)),
            "dia" => AtMorning(localStart.Date.AddDays(-amount), timezone),
            "semana" => AtMorning(localStart.Date.AddDays(-7 * amount), timezone),
            _ => null,
        };
    }

    /// <summary>Minutos u horas antes del instante de inicio, sin pasar por la hora local.</summary>
    private static DateTime Before(CalendarEvent calendarEvent, DateTime localStart, string? timezone, TimeSpan offset)
    {
        var startUtc = calendarEvent.IsAllDay ? UserClock.ToUtc(localStart, timezone) : calendarEvent.StartUtc;
        return startUtc - offset;
    }

    private static DateTime AtMorning(DateTime localDate, string? timezone) =>
        UserClock.ToUtc(localDate.Add(ReminderWording.DefaultTimeOfDay), timezone);
}
