using Ical.Net.DataTypes;
using Synap.Domain;
// Ical.Net tiene su propio CalendarEvent; el nuestro es el del dominio, así que el suyo es un VEVENT.
using VEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// Extrae de un recurso `.ics` las columnas por las que se filtra la copia (calendar-store-and-view
/// design.md Decisión 1). Lo único que tienen que garantizar es no dejar fuera a un candidato: el
/// lector vuelve a leer el `.ics` entero y es quien decide qué ocurrencias caen en el intervalo.
///
/// Las horas flotantes (sin zona) y los días de todo el día se guardan como si fueran UTC: su
/// instante real depende de la zona del usuario, y el lector amplía el intervalo un día por cada
/// lado para cubrir cualquier desfase.
/// </summary>
public static class IcsFields
{
    /// <summary>
    /// Ocurrencias que se recorren como mucho para hallar el fin de una repetición finita. Más allá
    /// se trata como si no tuviera fin, que solo cuesta que sea candidata más veces.
    /// </summary>
    public const int MaxOccurrencesForEnd = 5000;

    /// <summary>Las columnas del recurso, o null si no tiene un VEVENT legible.</summary>
    public static CalendarEventFields? TryRead(string ics)
    {
        Ical.Net.Calendar? calendar;
        try
        {
            calendar = Ical.Net.Calendar.Load(ics);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }

        var events = calendar?.Events.ToList() ?? [];
        var master = events.FirstOrDefault(e => e.RecurrenceId is null) ?? events.FirstOrDefault();
        if (master?.DtStart is not { } dtStart)
        {
            return null;
        }

        var isRecurring = master.RecurrenceRules.Count > 0
            || master.RecurrenceDates.GetAllDates().Any()
            || master.RecurrenceDates.GetAllPeriods().Any();
        var startsAt = LooseUtc(dtStart);

        DateTime? endsAt;
        try
        {
            endsAt = isRecurring ? LastOccurrenceEnd(calendar!, master, events) : EndOf(master, startsAt);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Una regla que Ical.Net no sabe recorrer: sin fin, para que nunca quede fuera.
            endsAt = null;
        }

        return new CalendarEventFields(
            master.Uid ?? string.Empty,
            string.IsNullOrWhiteSpace(master.Summary) ? null : master.Summary.Trim(),
            startsAt,
            endsAt,
            isRecurring,
            AllDay: !dtStart.HasTime);
    }

    /// <summary>
    /// El fin de la última ocurrencia, contando las excepciones movidas; null si alguna regla no
    /// tiene `UNTIL` ni `COUNT`, o si la serie es demasiado larga para recorrerla.
    /// </summary>
    private static DateTime? LastOccurrenceEnd(Ical.Net.Calendar calendar, VEvent master, IReadOnlyList<VEvent> events)
    {
        if (master.RecurrenceRules.Any(r => r.Until is null && r.Count is null))
        {
            return null;
        }

        DateTime? last = null;
        var seen = 0;
        foreach (var occurrence in calendar.GetOccurrences<VEvent>(null, null))
        {
            if (++seen > MaxOccurrencesForEnd)
            {
                return null;
            }

            var start = occurrence.Period.StartTime;
            if (start is null)
            {
                continue;
            }

            var end = occurrence.Period.EffectiveEndTime is { } effective
                ? LooseUtc(effective)
                : DefaultEnd(start, LooseUtc(start));
            last = last is null || end > last ? end : last;
        }

        // Una excepción `RECURRENCE-ID` movida más allá del final de la regla también cuenta.
        foreach (var exception in events.Where(e => e.RecurrenceId is not null && e.DtStart is not null))
        {
            var end = EndOf(exception, LooseUtc(exception.DtStart!));
            last = last is null || end > last ? end : last;
        }

        return last;
    }

    private static DateTime EndOf(VEvent component, DateTime startsAtUtc)
    {
        if (component.DtEnd is { } dtEnd)
        {
            return Max(LooseUtc(dtEnd), startsAtUtc);
        }

        if (component.Duration is { } duration && component.DtStart is { } dtStart)
        {
            return Max(LooseUtc(dtStart.Add(duration)), startsAtUtc);
        }

        return DefaultEnd(component.DtStart!, startsAtUtc);
    }

    /// <summary>Sin fin ni duración: un día si es de todo el día, un instante si tiene hora (RFC 5545, 3.6.1).</summary>
    private static DateTime DefaultEnd(CalDateTime start, DateTime startsAtUtc)
        => start.HasTime ? startsAtUtc : startsAtUtc.AddDays(1);

    /// <summary>UTC si la hora tiene zona; el valor tal cual si es flotante o es solo un día.</summary>
    internal static DateTime LooseUtc(CalDateTime value)
    {
        if (!value.HasTime)
        {
            return value.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        }

        return value.IsFloating
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            : DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
