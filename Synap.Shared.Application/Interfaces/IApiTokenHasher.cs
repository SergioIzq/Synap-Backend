namespace Synap.Shared.Application.Interfaces;

/// <summary>
/// Generates and verifies the personal-access-token used for non-interactive authentication
/// (the iOS Shortcut's quick-capture call) - ported from Kash's token-personal-api feature.
/// Only the hash is ever persisted; the plaintext token is shown to the user exactly once.
/// </summary>
public interface IApiTokenHasher
{
    (string PlainToken, string Hash) GenerateToken();
    string Hash(string plainToken);
}
