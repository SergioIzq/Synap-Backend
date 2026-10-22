namespace Synap.Domain;

/// <summary>
/// Un evento del calendario conectado de un usuario (calendar-integration design.md Decisión 1).
/// Nunca se persiste: vive lo que dura la lectura que lo trajo (specs/calendar-integration "Los
/// eventos nunca se almacenan"). Por eso no lleva identificador del proveedor.
///
/// <see cref="AllDayDate"/> tiene valor cuando el evento dura todo el día: es el día anotado en el
/// calendario, que no debe moverse con la zona horaria del usuario. En ese caso
/// <see cref="StartUtc"/> y <see cref="EndUtc"/> son la medianoche UTC de ese día y del siguiente,
/// solo para poder ordenar.
/// </summary>
public sealed record CalendarEvent(
    string Title,
    DateTime StartUtc,
    DateTime EndUtc,
    DateOnly? AllDayDate = null,
    string? Location = null,
    string? Description = null)
{
    public bool IsAllDay => AllDayDate is not null;

    /// <summary>Un evento de todo el día del <paramref name="date"/> anotado, que dura <paramref name="days"/> días.</summary>
    public static CalendarEvent AllDay(
        string title, DateOnly date, int days = 1, string? location = null, string? description = null)
    {
        var start = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return new CalendarEvent(title, start, start.AddDays(Math.Max(1, days)), date, location, description);
    }
}

/// <summary>De dónde viene la conexión. Solo lo conocen la conexión y el despachador de lectura.</summary>
public enum CalendarProvider
{
    Google,
    CalDav,
}
