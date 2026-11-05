using System.Collections.Concurrent;
using Synap.Application.Features.Calendar;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// Límite en memoria: se reinicia con el proceso, que es suficiente para una limitación de UX.
/// </summary>
public sealed class InMemoryManualSyncRateLimit : IManualSyncRateLimit
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<Guid, DateTime> _lastRequest = new();

    public int TryRequest(Guid userId)
    {
        var now = DateTime.UtcNow;

        if (_lastRequest.TryGetValue(userId, out var last))
        {
            var elapsed = now - last;
            if (elapsed < MinInterval)
            {
                return (int)(MinInterval - elapsed).TotalSeconds + 1;
            }
        }

        _lastRequest[userId] = now;
        return 0;
    }
}
