using Synap.Domain;

namespace Synap.Application.Features.Calendar;

/// <summary>Una conexión nueva reemplaza a la anterior, credenciales incluidas (specs "Cambio de proveedor").</summary>
internal static class CalendarConnections
{
    public static async Task ReplaceAsync(
        ICalendarConnectionWriteRepository connections,
        Guid userId,
        CalendarProvider provider,
        string account,
        string calendarRef,
        string credentialsEncrypted,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var existing = await connections.GetByIdAsync(userId, cancellationToken);
        if (existing is null)
        {
            connections.Add(CalendarConnection.Create(userId, provider, account, calendarRef, credentialsEncrypted, nowUtc));
            return;
        }

        existing.Replace(provider, account, calendarRef, credentialsEncrypted, nowUtc);
        connections.Update(existing);
    }
}
