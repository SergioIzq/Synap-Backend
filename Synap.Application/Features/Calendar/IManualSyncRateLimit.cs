namespace Synap.Application.Features.Calendar;

/// <summary>
/// Controla que un usuario no pida sincronizaciones manuales con demasiada frecuencia
/// (design.md Decisión 6: 429 si llega antes de 1 minuto).
/// La implementación puede ser en memoria; es una limitación de UX, no de seguridad.
/// </summary>
public interface IManualSyncRateLimit
{
    /// <summary>
    /// Registra la petición y devuelve cuántos segundos quedan de espera.
    /// 0 → se puede sincronizar; &gt; 0 → hay que esperar ese tiempo.
    /// </summary>
    int TryRequest(Guid userId);
}
