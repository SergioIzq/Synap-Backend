namespace Synap.Domain;

public enum CalendarProvider
{
    Google,
    CalDav,
}

/// <summary>
/// Un evento leído del calendario del usuario en el momento de usarlo (calendar-integration
/// design.md Decisión 1). Nunca se persiste: existe solo mientras dura la petición que lo leyó
/// (specs/calendar-integration "Los eventos nunca se almacenan").
///
/// <see cref="AllDayDate"/> está solo en los eventos de todo el día, y es el día tal como está
/// anotado: un evento del 14 de noviembre pertenece al 14 en cualquier zona horaria, así que no se
/// deriva de <see cref="StartUtc"/>.
/// </summary>
public sealed record CalendarEvent(
    string Title,
    DateTime StartUtc,
    DateTime EndUtc,
    DateOnly? AllDayDate,
    string? Location,
    string? Description)
{
    public bool IsAllDay => AllDayDate is not null;
}

/// <summary>
/// Lo que devuelve una lectura del calendario. Un tipo cerrado en vez de excepciones, para que el
/// asistente y el briefing tengan que tratar cada caso (design.md Decisión 1).
/// </summary>
public abstract record CalendarReadResult
{
    private CalendarReadResult()
    {
    }

    /// <summary>El usuario no tiene ningún calendario conectado.</summary>
    public sealed record NotConnected : CalendarReadResult;

    /// <summary>El acceso se revocó, caducó o las credenciales ya no sirven: hay que reconectar.</summary>
    public sealed record NeedsReconnect : CalendarReadResult;

    /// <summary>Fallo temporal (proveedor caído, plazo superado); la conexión sigue siendo válida.</summary>
    public sealed record Unavailable(string Cause) : CalendarReadResult;

    public sealed record Ok(IReadOnlyList<CalendarEvent> Events) : CalendarReadResult;
}
