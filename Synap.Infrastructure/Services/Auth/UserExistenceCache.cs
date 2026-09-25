using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Infrastructure.Services.Auth;

/// <summary>
/// Singleton (so the cache outlives requests); resolves the scoped repository per lookup.
/// One minute is the worst-case window in which a just-deleted account's token still works on
/// another instance - today there is only one, and deletion invalidates this one directly.
/// </summary>
public sealed class UserExistenceCache : IUserExistenceCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;

    public UserExistenceCache(IMemoryCache cache, IServiceScopeFactory scopeFactory)
    {
        _cache = cache;
        _scopeFactory = scopeFactory;
    }

    public async Task<bool> ExistsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(Key(userId), out bool exists))
        {
            return exists;
        }

        using var scope = _scopeFactory.CreateScope();
        exists = await scope.ServiceProvider.GetRequiredService<IUserReadRepository>().ExistsAsync(userId, cancellationToken);

        _cache.Set(Key(userId), exists, Ttl);
        return exists;
    }

    public void Invalidate(Guid userId) => _cache.Remove(Key(userId));

    private static string Key(Guid userId) => $"user-exists:{userId}";
}
