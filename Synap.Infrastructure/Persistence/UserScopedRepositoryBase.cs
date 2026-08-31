using SergioIzq.Application.Kernel.Interfaces;

namespace Synap.Infrastructure.Persistence;

/// <summary>
/// Base for every repository/query that must be scoped to the currently authenticated user
/// (see specs/identity, specs/knowledge-vault and specs/ai-assistant "per-user isolation").
/// Every future repository touching notes, tags or embeddings should derive from this and
/// filter by <see cref="CurrentUserId"/> rather than re-deriving the current user ad hoc, so
/// isolation can't be forgotten one feature at a time.
/// </summary>
public abstract class UserScopedRepositoryBase
{
    private readonly IUserContext _userContext;

    protected UserScopedRepositoryBase(IUserContext userContext)
    {
        _userContext = userContext;
    }

    protected Guid CurrentUserId => _userContext.UserId
        ?? throw new InvalidOperationException(
            "No authenticated user in context - a user-scoped repository must only be used within an authenticated request.");
}
