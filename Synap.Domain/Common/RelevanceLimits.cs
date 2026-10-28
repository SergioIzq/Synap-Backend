namespace Synap.Domain;

/// <summary>
/// Umbral de relevancia compartido por conexiones, rescate y briefing de calendario.
/// Mueve el valor aquí (smart-resurfacing, Decisión 1) para que las tres rutas lo lean del mismo
/// sitio: si algún día cambia, cambia para todas.
/// Calibrado contra una bóveda real en la tarea 1.13 (task 1.13).
/// </summary>
public static class RelevanceLimits
{
    /// <summary>
    /// Similitud mínima para que dos notas se consideren relacionadas. Por encima del umbral de
    /// las preguntas del asistente (0,45) a propósito: nadie pidió estas notas.
    ///
    /// Calibración (1.13): revisado contra una bóveda de ~80 notas en español. Pares por encima
    /// de 0,55 corresponden a notas sobre el mismo tema técnico o proyecto; entre 0,45 y 0,55 ya
    /// aparecen coincidencias léxicas sin relación conceptual real (p. ej. dos notas que usan
    /// «deploy» en contextos distintos). El valor 0,55 eliminó los falsos positivos sin perder
    /// ninguna conexión genuina en el conjunto de prueba.
    /// </summary>
    public const double RelatedNoteSimilarityFloor = 0.55;

    /// <summary>Máximo de conexiones que devuelve <c>GET /api/notes/{id}/connections</c>.</summary>
    public const int MaxConnections = 5;
}
