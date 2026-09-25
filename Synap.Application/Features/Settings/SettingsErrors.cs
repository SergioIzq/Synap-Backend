using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;

namespace Synap.Application.Features.Settings;

public static class SettingsErrors
{
    public static readonly Error UserNotFound = Error.NotFound("Usuario no encontrado.");
    public static readonly Error GroqKeyRequired = Error.Validation("Introduce tu API key de Groq.");
    public static readonly Error GroqKeyNotConfigured = Error.Validation("Configura primero tu API key de Groq.");
    public static readonly Error GroqKeyInvalid = Error.Validation("La API key de Groq no es válida. Revisa que la hayas copiado completa.");
    public static readonly Error GroqKeyUnreadable = Error.Validation("No se pudo leer tu API key guardada. Vuelve a introducirla.");
    public static readonly Error GroqRateLimited = Error.Validation("Tu API key de Groq ha alcanzado su límite. Inténtalo de nuevo en un momento.");
    public static readonly Error GroqUnavailable = Error.Validation("No se pudo contactar con Groq para validar la API key. Inténtalo de nuevo más tarde.");
    public static readonly Error GroqModelUnknown = Error.Validation("Ese modelo no está disponible para tu API key.");

    /// <summary>Maps a failed key check to the error the user sees; Ok is not a failure.</summary>
    public static Error FromKeyStatus(LlmKeyStatus status) => status switch
    {
        LlmKeyStatus.InvalidKey => GroqKeyInvalid,
        LlmKeyStatus.RateLimited => GroqRateLimited,
        _ => GroqUnavailable,
    };
}
