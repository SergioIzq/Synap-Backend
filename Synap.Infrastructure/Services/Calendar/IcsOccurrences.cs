using Ical.Net;
using Ical.Net.DataTypes;
using Synap.Domain;
// Ical.Net tiene su propio CalendarEvent; el nuestro es el del dominio, así que el suyo es un VEVENT.
using VEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// Las ocurrencias de un recurso `.ics` guardado dentro de un intervalo (calendar-store-and-view
/// design.md Decisión 5). Ical.Net expande la regla con sus excepciones (`EXDATE` y las
/// `RECURRENCE-ID` movidas), y aquí se descartan los cancelados y los rechazados por el usuario.
///
/// Las horas flotantes se leen en la zona del usuario. Un evento de todo el día pertenece a los
/// días anotados, comparados con los días locales del intervalo y no con instantes UTC, para que
/// no salte al día anterior o al siguiente por la zona.
/// </summary>
public static class IcsOccurrences
{
    /// <summary>Lo que se expande de un solo recurso como mucho, para que una regla rara no cueste la lectura.</summary>
    public const int MaxOccurrencesPerResource = 500;

    public static IEnumerable<CalendarEvent> Expand(
        string ics, string? username, string? timezone, DateTime fromUtc, DateTime toUtc)
    {
        var calendar = Ical.Net.Calendar.Load(ics);
        if (calendar is null)
        {
            yield break;
        }

        var firstDay = DateOnly.FromDateTime(UserClock.ToLocal(fromUtc, timezone));
        var lastDayExclusive = DateOnly.FromDateTime(UserClock.ToLocal(toUtc.AddTicks(-1), timezone)).AddDays(1);

        // Un día de margen a cada lado: las horas flotantes y los días se comparan sin zona.
        var occurrences = calendar
            .GetOccurrences<VEvent>(new CalDateTime(DateTime.SpecifyKind(fromUtc.AddDays(-1), DateTimeKind.Utc)), null)
            .TakeWhileBefore(new CalDateTime(DateTime.SpecifyKind(toUtc.AddDays(1), DateTimeKind.Utc)))
            .Take(MaxOccurrencesPerResource);

        foreach (var occurrence in occurrences)
        {
            if (occurrence.Source is not VEvent source || IsCancelled(source) || DeclinedBy(source, username))
            {
                continue;
            }

            var mapped = Map(source, occurrence.Period, timezone);
            if (mapped is null)
            {
                continue;
            }

            var inRange = mapped.AllDayDate is { } day
                ? day < lastDayExclusive && day.AddDays(Days(mapped)) > firstDay
                : mapped.StartUtc < toUtc && (mapped.EndUtc > fromUtc || (mapped.EndUtc == mapped.StartUtc && mapped.StartUtc >= fromUtc));

            if (inRange)
            {
                yield return mapped;
            }
        }
    }

    private static int Days(CalendarEvent allDay) => Math.Max(1, (int)(allDay.EndUtc - allDay.StartUtc).TotalDays);

    private static bool IsCancelled(VEvent source) =>
        string.Equals(source.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rechazado por el propio usuario: un ATTENDEE con su dirección y `PARTSTAT=DECLINED`. Si el
    /// usuario no coincide con ningún asistente, el evento se muestra (calendar-integration
    /// design.md, Riesgos).
    /// </summary>
    private static bool DeclinedBy(VEvent source, string? username) =>
        !string.IsNullOrWhiteSpace(username)
        && source.Attendees.Any(a =>
            string.Equals(a.ParticipationStatus, "DECLINED", StringComparison.OrdinalIgnoreCase)
            && a.Value is { } address
            && string.Equals(StripMailto(address.OriginalString), username.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string StripMailto(string address) =>
        address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? address[7..].Trim() : address.Trim();

    private static CalendarEvent? Map(VEvent source, Period period, string? timezone)
    {
        var start = period.StartTime;
        if (start is null)
        {
            return null;
        }

        var title = string.IsNullOrWhiteSpace(source.Summary) ? "(sin título)" : source.Summary.Trim();
        var location = string.IsNullOrWhiteSpace(source.Location) ? null : source.Location.Trim();
        var description = string.IsNullOrWhiteSpace(source.Description) ? null : source.Description.Trim();
        var recurring = source.RecurrenceRules.Count > 0 || source.RecurrenceId is not null
            || source.RecurrenceDates.GetAllDates().Any() || source.RecurrenceDates.GetAllPeriods().Any();
        var end = period.EffectiveEndTime;

        // `VALUE=DATE`: el día anotado, sin zona horaria.
        if (!start.HasTime)
        {
            var days = end is { HasTime: false } ? end.Date.DayNumber - start.Date.DayNumber : 1;
            return CalendarEvent.AllDay(title, start.Date, days, location, description) with { IsRecurring = recurring };
        }

        var startUtc = ToUtc(start, timezone);
        var endUtc = end is { HasTime: true } ? ToUtc(end, timezone) : startUtc;

        return new CalendarEvent(title, startUtc, endUtc < startUtc ? startUtc : endUtc, null, location, description, IsRecurring: recurring);
    }

    /// <summary>Con zona, su instante; flotante, la misma hora en la zona del usuario.</summary>
    private static DateTime ToUtc(CalDateTime value, string? timezone) =>
        value.IsFloating
            ? DateTime.SpecifyKind(UserClock.ToUtc(DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified), timezone), DateTimeKind.Utc)
            : DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc);
}
