namespace Synap.Domain;

/// <summary>
/// Una ocurrencia de un evento del calendario de un usuario, leída de la copia
/// (calendar-store-and-view design.md Decisión 5). Una repetición devuelve una por ocurrencia.
///
/// <see cref="AllDayDate"/> tiene valor cuando el evento dura todo el día: es el día anotado en el
/// calendario, que no debe moverse con la zona horaria del usuario. En ese caso
/// <see cref="StartUtc"/> y <see cref="EndUtc"/> son la medianoche UTC de ese día y del siguiente,
/// solo para poder ordenar.
///
/// <see cref="Calendar"/> es el calendario del que viene, con su color y su ámbito; null solo en
/// los eventos que construyen los tests.
/// </summary>
public sealed record CalendarEvent(
    string Title,
    DateTime StartUtc,
    DateTime EndUtc,
    DateOnly? AllDayDate = null,
    string? Location = null,
    string? Description = null,
    CalendarEventSource? Calendar = null,
    bool IsRecurring = false)
{
    public bool IsAllDay => AllDayDate is not null;

    /// <summary>Personal si no se sabe de qué calendario viene.</summary>
    public CalendarScope Scope => Calendar?.Scope ?? CalendarScope.Personal;

    /// <summary>Un evento de todo el día del <paramref name="date"/> anotado, que dura <paramref name="days"/> días.</summary>
    public static CalendarEvent AllDay(
        string title, DateOnly date, int days = 1, string? location = null, string? description = null)
    {
        var start = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return new CalendarEvent(title, start, start.AddDays(Math.Max(1, days)), date, location, description);
    }
}

/// <summary>El calendario de un evento: lo que la vista y el briefing necesitan para pintarlo y agruparlo.</summary>
public sealed record CalendarEventSource(Guid CalendarId, string Name, string Color, CalendarScope Scope);

/// <summary>De dónde viene la conexión. Solo lo conocen la conexión y el despachador de lectura.</summary>
public enum CalendarProvider
{
    CalDav,
}
