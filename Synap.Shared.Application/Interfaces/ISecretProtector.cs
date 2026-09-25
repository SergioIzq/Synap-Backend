namespace Synap.Shared.Application.Interfaces;

/// <summary>
/// Symmetric encryption for per-user secrets at rest (today: each user's own Groq API key -
/// byok-groq-and-settings design.md Decision 1). Unlike IApiTokenHasher, the value must be
/// recoverable, since it's sent on to the provider on every assistant question.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>
    /// False when the value was tampered with, has an unknown format, or was encrypted with a
    /// different master key (e.g. after losing SECRETS_ENCRYPTION_KEY) - never throws for that.
    /// </summary>
    bool TryUnprotect(string protectedValue, out string plaintext);
}
