using Synap.Domain;
using Synap.Shared.Application.Interfaces;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synap.Application.Features.Calendar;

/// <summary>
/// Lo que una conexión necesita para leer, con la forma de su proveedor (calendar-integration
/// design.md Decisión 2). Dónde se lee (el id `primary` o la URL de la colección) no es secreto y
/// va en <see cref="CalendarConnection.CalendarRef"/>.
/// </summary>
public abstract record CalendarCredentials
{
    private CalendarCredentials()
    {
    }

    public abstract CalendarProvider Provider { get; }

    /// <summary>El refresh token y el último access token con su caducidad (design.md Decisión 3).</summary>
    public sealed record Google(string RefreshToken, string? AccessToken = null, DateTime? AccessTokenExpiresAt = null)
        : CalendarCredentials
    {
        public override CalendarProvider Provider => CalendarProvider.Google;

        public override string ToString() => "Google { *** }";
    }

    public sealed record CalDav(string Username, string Password) : CalendarCredentials
    {
        public override CalendarProvider Provider => CalendarProvider.CalDav;

        public override string ToString() => $"CalDav {{ Username = {Username}, Password = *** }}";
    }
}

/// <summary>
/// Convierte las credenciales en el único blob cifrado de la conexión y al revés, con el mismo
/// <see cref="ISecretProtector"/> y la misma clave maestra que la key de Groq.
///
/// Leer nunca lanza: un blob cifrado con otra clave maestra, manipulado o con otra forma vuelve
/// como null, que el despachador trata como <see cref="CalendarReadResult.NeedsReconnect"/>.
/// </summary>
public sealed class CalendarCredentialsProtector
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ISecretProtector _secretProtector;

    public CalendarCredentialsProtector(ISecretProtector secretProtector)
    {
        _secretProtector = secretProtector;
    }

    public string Protect(CalendarCredentials credentials) => _secretProtector.Protect(credentials switch
    {
        CalendarCredentials.Google google => JsonSerializer.Serialize(google, Json),
        CalendarCredentials.CalDav calDav => JsonSerializer.Serialize(calDav, Json),
        _ => throw new ArgumentOutOfRangeException(nameof(credentials)),
    });

    public CalendarCredentials? TryRead(CalendarProvider provider, string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue) || !_secretProtector.TryUnprotect(protectedValue, out var json))
        {
            return null;
        }

        try
        {
            return provider switch
            {
                CalendarProvider.Google => Validated(JsonSerializer.Deserialize<CalendarCredentials.Google>(json, Json)),
                CalendarProvider.CalDav => Validated(JsonSerializer.Deserialize<CalendarCredentials.CalDav>(json, Json)),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CalendarCredentials? Validated(CalendarCredentials? credentials) => credentials switch
    {
        CalendarCredentials.Google { RefreshToken: var token } when string.IsNullOrWhiteSpace(token) => null,
        CalendarCredentials.CalDav { Username: var user, Password: var password }
            when string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password) => null,
        _ => credentials,
    };
}
