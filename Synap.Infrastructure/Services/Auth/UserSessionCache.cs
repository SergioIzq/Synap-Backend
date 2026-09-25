using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Infrastructure.Services.Auth;

/// <summary>
/// Singleton (so the cache outlives requests); resolves the scoped repository per lookup.
/// One minute is the worst-case window in which a stale session still works on another
/// instance - today there is only one, and every stamp change invalidates this one directly.
/// </summary>
public sealed class UserSessionCache : IUserSessionCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;

    public UserSessionCache(IMemoryCache cache, IServiceScopeFactory scopeFactory)
    {
        _cache = cache;
        _scopeFactory = scopeFactory;
    }

    public async Task<string?> GetSecurityStampAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(Key(userId), out string? stamp))
        {
            return stamp;
        }

        using var scope = _scopeFactory.CreateScope();
        stamp = await scope.ServiceProvider.GetRequiredService<IUserReadRepository>().GetSecurityStampAsync(userId, cancellationToken);

        _cache.Set(Key(userId), stamp, Ttl);
        return stamp;
    }

    public void Invalidate(Guid userId) => _cache.Remove(Key(userId));

    private static string Key(Guid userId) => $"user-stamp:{userId}";
}
