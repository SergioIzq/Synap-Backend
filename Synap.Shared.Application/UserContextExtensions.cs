using SergioIzq.Application.Kernel.Interfaces;

namespace Synap.Shared.Application;

/// <summary>
/// Replaces the earlier UserScopedRepositoryBase (task 2.6): an inheritance-based helper
/// couldn't work once repositories also need to derive from Infrastructure's
/// AbsWriteRepository&lt;TEntity, TId&gt; (C# has no multiple class inheritance), and Application
/// handlers need the same check but must not depend on Infrastructure at all. An extension
/// method on the kernel's IUserContext works everywhere, regardless of layer or base class.
/// Every repository/query/handler touching notes, tags or embeddings must resolve the current
/// user through this rather than re-deriving it ad hoc, so isolation can't be forgotten one
/// feature at a time (specs/knowledge-vault, specs/ai-assistant "per-user isolation").
/// </summary>
public static class UserContextExtensions
{
    public static Guid RequireUserId(this IUserContext userContext)
        => userContext.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user in context - this operation must only run within an authenticated request.");
}
