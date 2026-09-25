using Microsoft.Extensions.Caching.Memory;
using Synap.Shared.Application.Interfaces;

namespace Synap.Infrastructure.Services.Auth;

/// <summary>
/// Fixed one-hour window per address, in memory (password-recovery design.md Decision 2):
/// resets on restart, which is acceptable with a single API instance.
/// </summary>
public sealed class MemoryRecoveryRequestLimiter : IRecoveryRequestLimiter
{
    public const int MaxPerHour = 3;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly IMemoryCache _cache;
    private readonly object _lock = new();

    public MemoryRecoveryRequestLimiter(IMemoryCache cache)
    {
        _cache = cache;
    }

    public bool TryAcquire(string normalizedEmail)
    {
        var key = $"recovery:{normalizedEmail}";
        lock (_lock)
        {
            var counter = _cache.GetOrCreate(key, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = Window;
                return new Counter();
            })!;

            if (counter.Count >= MaxPerHour)
            {
                return false;
            }

            counter.Count++;
            return true;
        }
    }

    private sealed class Counter
    {
        public int Count;
    }
}
