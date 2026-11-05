using SergioIzq.Domain.Kernel.Abstractions;
using Synap.Shared.Domain.ValueObjects.Ids;
using System.ComponentModel.DataAnnotations.Schema;

namespace Synap.Domain;

/// <summary>
/// Un recurso `.ics` de un calendario del servidor, tal como llegó (calendar-store-and-view
/// design.md Decisión 1): el maestro con sus excepciones `RECURRENCE-ID`, y no sus ocurrencias.
/// Se guarda entero porque escribir (calendar-write) necesita el `href`, el `ETag` y el original.
///
/// Las columnas derivadas (<see cref="CalendarEventFields"/>) solo sirven para elegir en SQL los
/// candidatos de un intervalo; lo que se devuelve sale siempre de <see cref="Ics"/>.
/// </summary>
[Table("calendar_events")]
public sealed class CalendarEventResource : AbsEntity<CalendarEventId>
{
    public const int MaxHrefLength = 2048;
    public const int MaxETagLength = 256;
    public const int MaxUidLength = 1024;
    public const int MaxSummaryLength = 1024;

    private CalendarEventResource() : base(CalendarEventId.Create(Guid.NewGuid()).Value)
    {
    }

    private CalendarEventResource(CalendarEventId id, CalendarId calendarId, UserId userId, string href) : base(id)
    {
        CalendarId = calendarId;
        UserId = userId;
        Href = href;
    }

    public CalendarId CalendarId { get; private set; }

    /// <summary>Repetido del calendario para filtrar y aislar sin joins.</summary>
    public UserId UserId { get; private set; }

    public string Href { get; private set; } = null!;
    public string? ETag { get; private set; }
    public string Uid { get; private set; } = null!;
    public string Ics { get; private set; } = null!;

    public string? Summary { get; private set; }

    /// <summary>Inicio del maestro en UTC; en un evento de todo el día, la medianoche UTC de su día.</summary>
    public DateTime StartsAtUtc { get; private set; }

    /// <summary>Fin de la última ocurrencia; null si la repetición no tiene fin.</summary>
    public DateTime? EndsAtUtc { get; private set; }

    public bool IsRecurring { get; private set; }
    public bool AllDay { get; private set; }

    public static CalendarEventResource Create(
        CalendarId calendarId, UserId userId, string href, string? etag, string ics, CalendarEventFields fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(href);

        var resource = new CalendarEventResource(CalendarEventId.Create(Guid.NewGuid()).Value, calendarId, userId, href);
        resource.Replace(etag, ics, fields);
        return resource;
    }

    /// <summary>El servidor manda una versión nueva: prevalece entera sobre la copia.</summary>
    public void Replace(string? etag, string ics, CalendarEventFields fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ics);

        ETag = Clip(etag, MaxETagLength);
        Ics = ics;
        Uid = Clip(fields.Uid, MaxUidLength) ?? string.Empty;
        Summary = Clip(fields.Summary, MaxSummaryLength);
        StartsAtUtc = DateTime.SpecifyKind(fields.StartsAtUtc, DateTimeKind.Utc);
        EndsAtUtc = fields.EndsAtUtc is { } end ? DateTime.SpecifyKind(end, DateTimeKind.Utc) : null;
        IsRecurring = fields.IsRecurring;
        AllDay = fields.AllDay;
    }

    private static string? Clip(string? text, int max)
        => string.IsNullOrEmpty(text) ? text : text.Length <= max ? text : text[..max];
}

/// <summary>Lo que se extrae de un `.ics` para filtrar sin leerlo (design.md Decisión 1).</summary>
public sealed record CalendarEventFields(
    string Uid,
    string? Summary,
    DateTime StartsAtUtc,
    DateTime? EndsAtUtc,
    bool IsRecurring,
    bool AllDay);
