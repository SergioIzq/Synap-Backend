using SergioIzq.Domain.Kernel.Abstractions;
using Synap.Shared.Domain.ValueObjects;
using Synap.Shared.Domain.ValueObjects.Ids;
using System.ComponentModel.DataAnnotations.Schema;

namespace Synap.Domain;

[Table("users")]
public sealed class User : AbsEntity<UserId>
{
    // Private constructor for EF Core materialization.
    private User() : base(UserId.Create(Guid.NewGuid()).Value)
    {
    }

    private User(UserId id, Email email, PasswordHash passwordHash) : base(id)
    {
        Email = email;
        PasswordHash = passwordHash;
    }

    public Email Email { get; private set; }
    public PasswordHash PasswordHash { get; private set; }
    // Creation timestamp is the inherited AbsEntity<TId>.FechaCreacion - no need to duplicate it.
    public string? ApiTokenHash { get; private set; }
    public DateTime? ApiTokenCreatedAt { get; private set; }

    public static User Create(Email email, PasswordHash passwordHash)
        => new(UserId.Create(Guid.NewGuid()).Value, email, passwordHash);

    /// <summary>
    /// Sets (or replaces) the user's personal access token hash - see specs/identity for the
    /// non-interactive-auth use case (the iOS Shortcut quick-capture call). Overwriting the
    /// previous hash is the only revocation mechanism: the old plaintext token stops matching.
    /// </summary>
    public void SetApiToken(string hash)
    {
        ApiTokenHash = hash;
        ApiTokenCreatedAt = DateTime.UtcNow;
    }
}
