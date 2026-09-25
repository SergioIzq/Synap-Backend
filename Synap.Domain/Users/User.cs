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

    // The user's own Groq API key (byok-groq-and-settings) - only ever stored encrypted; the
    // last four characters are kept apart so the UI can show a masked form without decrypting.
    public string? GroqApiKeyEncrypted { get; private set; }
    public string? GroqApiKeyLast4 { get; private set; }
    public DateTime? GroqApiKeyUpdatedAt { get; private set; }
    // Null means "use the server's default model".
    public string? GroqModel { get; private set; }

    public bool HasGroqApiKey => GroqApiKeyEncrypted is not null;

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

    /// <summary>
    /// Sets (or replaces) the user's own Groq API key. Takes the already-encrypted value: the
    /// domain never sees the plaintext key, only the caller that validated and encrypted it.
    /// </summary>
    public void SetGroqApiKey(string encryptedKey, string last4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(last4);

        GroqApiKeyEncrypted = encryptedKey;
        GroqApiKeyLast4 = last4;
        GroqApiKeyUpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Removes the key and the model chosen for it - a new key may not offer that model.</summary>
    public void ClearGroqApiKey()
    {
        GroqApiKeyEncrypted = null;
        GroqApiKeyLast4 = null;
        GroqApiKeyUpdatedAt = null;
        GroqModel = null;
    }

    /// <summary>Null resets to the server's default model.</summary>
    public void SetGroqModel(string? model)
    {
        GroqModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
    }
}
