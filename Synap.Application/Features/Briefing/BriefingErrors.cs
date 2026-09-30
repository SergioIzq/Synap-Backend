using SergioIzq.Domain.Kernel.Abstractions.Results;

namespace Synap.Application.Features.Briefing;

public static class BriefingErrors
{
    public static readonly Error UserNotFound = Error.NotFound("Usuario no encontrado.");

    /// <summary>
    /// The one part of a briefing that does not arrive that the user can fix themselves, so it is
    /// the one that is said to them rather than only recorded (design.md Decision 6).
    /// </summary>
    public static readonly Error TelegramNotConnected =
        Error.Validation("Conecta Telegram en Configuración para recibir el briefing: es por donde se envía.");

    public static readonly Error NotDelivered =
        Error.Validation("No se ha podido enviar el briefing por Telegram. Inténtalo de nuevo en un momento.");
}
