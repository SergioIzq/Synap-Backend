using SergioIzq.Domain.Kernel.Abstractions;
using Synap.Shared.Domain.ValueObjects.Ids;
using System.ComponentModel.DataAnnotations.Schema;

namespace Synap.Domain;

/// <summary>
/// El calendario conectado de un usuario (specs/calendar-integration). Su identificador es el del
/// propio usuario: como mucho hay una conexión por usuario, y la clave lo garantiza (design.md
/// Decisión 2). Las credenciales llegan ya cifradas; el dominio nunca ve un token ni una contraseña.
/// </summary>
[Table("calendar_connections")]
public sealed class CalendarConnection : AbsEntity<UserId>
{
    private CalendarConnection() : base(UserId.Create(Guid.NewGuid()).Value)
    {
    }

    private CalendarConnection(UserId userId) : base(userId)
    {
    }

    public CalendarProvider Provider { get; private set; }

    /// <summary>El email (Google) o el usuario (CalDAV), solo para mostrarlo en Configuración.</summary>
    public string AccountLabel { get; private set; } = null!;

    /// <summary>`primary` en Google; la URL de la colección en CalDAV.</summary>
    public string CalendarRef { get; private set; } = null!;

    public string CredentialsEncrypted { get; private set; } = null!;
    public DateTime ConnectedAt { get; private set; }

    /// <summary>Desde cuándo la conexión dejó de funcionar y pide reconectar; null mientras funciona.</summary>
    public DateTime? NeedsReconnectSince { get; private set; }

    public bool NeedsReconnect => NeedsReconnectSince is not null;

    public static CalendarConnection Create(
        UserId userId, CalendarProvider provider, string accountLabel, string calendarRef, string credentialsEncrypted, DateTime nowUtc)
    {
        var connection = new CalendarConnection(userId);
        connection.Reconnect(provider, accountLabel, calendarRef, credentialsEncrypted, nowUtc);
        return connection;
    }

    /// <summary>
    /// Sustituye la conexión entera, también de proveedor: las credenciales anteriores se pierden
    /// (specs/calendar-integration "Cambio de proveedor") y la marca de reconexión desaparece.
    /// </summary>
    public void Reconnect(CalendarProvider provider, string accountLabel, string calendarRef, string credentialsEncrypted, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsEncrypted);

        Provider = provider;
        AccountLabel = accountLabel.Trim();
        CalendarRef = calendarRef;
        CredentialsEncrypted = credentialsEncrypted;
        ConnectedAt = nowUtc;
        NeedsReconnectSince = null;
    }

    /// <summary>Tras renovar un access token; no cambia nada más de la conexión.</summary>
    public void UpdateCredentials(string credentialsEncrypted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsEncrypted);
        CredentialsEncrypted = credentialsEncrypted;
    }

    /// <summary>Conserva el primer momento: marcarla de nuevo en cada lectura fallida no lo mueve.</summary>
    public void MarkNeedsReconnect(DateTime nowUtc) => NeedsReconnectSince ??= nowUtc;

    public void ClearNeedsReconnect() => NeedsReconnectSince = null;
}
