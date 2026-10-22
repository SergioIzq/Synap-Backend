using SergioIzq.Domain.Kernel.Abstractions;
using Synap.Shared.Domain.ValueObjects.Ids;
using System.ComponentModel.DataAnnotations.Schema;

namespace Synap.Domain;

/// <summary>
/// La conexión de calendario de un usuario (calendar-integration design.md Decisión 2). Su clave
/// es el propio id del usuario, lo que garantiza una conexión como mucho por usuario; conectar otra
/// la reemplaza entera.
///
/// Las credenciales son un único JSON cifrado cuya forma decide el proveedor; el dominio nunca mira
/// dentro. <see cref="AccountLabel"/> y <see cref="CalendarRef"/> no son secretos: son lo que
/// Configuración muestra y dónde se lee.
/// </summary>
[Table("calendar_connections")]
public sealed class CalendarConnection : AbsEntity<UserId>
{
    private CalendarConnection() : base(UserId.Create(Guid.NewGuid()).Value)
    {
    }

    private CalendarConnection(
        UserId userId,
        CalendarProvider provider,
        string accountLabel,
        string calendarRef,
        string credentialsEncrypted,
        DateTime connectedAtUtc) : base(userId)
    {
        Provider = provider;
        AccountLabel = accountLabel;
        CalendarRef = calendarRef;
        CredentialsEncrypted = credentialsEncrypted;
        ConnectedAt = connectedAtUtc;
    }

    public UserId UserId => Id;
    public CalendarProvider Provider { get; private set; }
    public string AccountLabel { get; private set; } = null!;
    public string CalendarRef { get; private set; } = null!;
    public string CredentialsEncrypted { get; private set; } = null!;
    public DateTime ConnectedAt { get; private set; }

    /// <summary>Desde cuándo la conexión necesita volver a conectarse; null si funciona.</summary>
    public DateTime? NeedsReconnectSince { get; private set; }

    public bool NeedsReconnect => NeedsReconnectSince is not null;

    public static CalendarConnection Create(
        Guid userId,
        CalendarProvider provider,
        string accountLabel,
        string calendarRef,
        string credentialsEncrypted,
        DateTime connectedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsEncrypted);

        return new CalendarConnection(
            UserId.CreateFromDatabase(userId),
            provider,
            accountLabel.Trim(),
            calendarRef.Trim(),
            credentialsEncrypted,
            connectedAtUtc);
    }

    /// <summary>
    /// Reemplaza la conexión entera por otra, quizá de otro proveedor: credenciales, cuenta,
    /// referencia y fecha nuevas, y sin marca de reconexión (specs/calendar-integration "Cambio de
    /// proveedor", "Reconexión").
    /// </summary>
    public void Replace(
        CalendarProvider provider, string accountLabel, string calendarRef, string credentialsEncrypted, DateTime connectedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsEncrypted);

        Provider = provider;
        AccountLabel = accountLabel.Trim();
        CalendarRef = calendarRef.Trim();
        CredentialsEncrypted = credentialsEncrypted;
        ConnectedAt = connectedAtUtc;
        NeedsReconnectSince = null;
    }

    /// <summary>
    /// Sustituye las credenciales cifradas sin tocar nada más; por ejemplo, al renovar el access
    /// token de Google (design.md Decisión 3).
    /// </summary>
    public void ReplaceCredentials(string credentialsEncrypted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsEncrypted);
        CredentialsEncrypted = credentialsEncrypted;
    }

    /// <summary>
    /// Marca la conexión para reconectar. Si ya estaba marcada, conserva el primer momento: es
    /// "desde cuándo" no funciona, y Configuración lo muestra así.
    /// </summary>
    public void MarkNeedsReconnect(DateTime nowUtc)
    {
        NeedsReconnectSince ??= nowUtc;
    }

    public void ClearNeedsReconnect()
    {
        NeedsReconnectSince = null;
    }
}
