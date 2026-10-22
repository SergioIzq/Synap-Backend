namespace Synap.Domain;

/// <summary>
/// El resultado de leer el calendario de un usuario (calendar-integration design.md Decisión 1).
/// Es un valor y no una excepción porque el asistente y el briefing tienen que hacer algo distinto
/// en cada caso, y un conjunto cerrado de cuatro casos obliga a tratarlos todos.
/// </summary>
public abstract record CalendarReadResult
{
    private CalendarReadResult()
    {
    }

    /// <summary>El usuario no tiene calendario conectado. No es un fallo.</summary>
    public sealed record NotConnected : CalendarReadResult;

    /// <summary>
    /// El acceso fue revocado, caducó o las credenciales ya no sirven: la conexión queda marcada y
    /// el usuario tiene que volver a conectarla (specs/calendar-integration "Acceso revocado").
    /// </summary>
    public sealed record NeedsReconnect : CalendarReadResult;

    /// <summary>
    /// Fallo temporal (el proveedor no responde o responde con error). No marca la conexión.
    /// <paramref name="Cause"/> es para los registros: nunca lleva contenido de un evento.
    /// </summary>
    public sealed record Unavailable(string Cause) : CalendarReadResult;

    /// <summary>Los eventos del intervalo pedido, que pueden ser ninguno.</summary>
    public sealed record Ok(IReadOnlyList<CalendarEvent> Events) : CalendarReadResult;
}
