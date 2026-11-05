namespace Synap.Domain;

/// <summary>
/// El resultado de leer el calendario de un usuario (calendar-store-and-view design.md Decisión 5).
/// Se lee siempre de la copia, así que con calendario conectado siempre hay respuesta: lo que
/// cambia es si la copia está al día, y eso lo dice <see cref="CalendarCopyState"/>.
/// </summary>
public abstract record CalendarReadResult
{
    private CalendarReadResult()
    {
    }

    /// <summary>El usuario no tiene calendario conectado. No es un fallo.</summary>
    public sealed record NotConnected : CalendarReadResult;

    /// <summary>Los eventos del intervalo pedido, que pueden ser ninguno, y el estado de la copia.</summary>
    public sealed record Ok(IReadOnlyList<CalendarEvent> Events, CalendarCopyState State) : CalendarReadResult;
}

/// <summary>
/// Hasta cuándo está al día la copia. <see cref="LastSyncedAt"/> es la última sincronización
/// correcta del calendario activo que lleva más tiempo sin sincronizarse; null si aún no se ha
/// sincronizado ninguno. <see cref="NeedsReconnect"/> es que la conexión está marcada, y
/// <see cref="SyncFailing"/>, que el último intento de algún calendario activo falló.
/// </summary>
public sealed record CalendarCopyState(DateTime? LastSyncedAt, bool NeedsReconnect, bool SyncFailing)
{
    public static CalendarCopyState Fresh(DateTime lastSyncedAt) => new(lastSyncedAt, false, false);

    /// <summary>La copia puede no tener lo último: hay que decirlo junto a los eventos.</summary>
    public bool IsStale => NeedsReconnect || SyncFailing;
}
