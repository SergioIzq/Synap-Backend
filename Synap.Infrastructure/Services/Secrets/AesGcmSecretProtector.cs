using Synap.Shared.Application.Interfaces;
using System.Security.Cryptography;
using System.Text;

namespace Synap.Infrastructure.Services.Secrets;

/// <summary>
/// AES-256-GCM with a single master key from configuration (SECRETS_ENCRYPTION_KEY) - see
/// byok-groq-and-settings design.md Decision 1 for why not ASP.NET Data Protection. Output
/// format is "v1:&lt;base64 nonce&gt;:&lt;base64 ciphertext+tag&gt;"; the version prefix leaves
/// room for a future master-key rotation without guessing which key encrypted what.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    public const int KeySizeBytes = 32;

    private const string Version = "v1";
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"The secrets encryption key must be exactly {KeySizeBytes} bytes.", nameof(key));
        }

        _key = key;
    }

    /// <summary>Parses the base64 master key from configuration, failing fast on anything invalid.</summary>
    public static AesGcmSecretProtector FromBase64(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            throw new InvalidOperationException(
                "Missing 'Secrets:EncryptionKey' configuration (SECRETS_ENCRYPTION_KEY) - generate one with `openssl rand -base64 32`.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("'Secrets:EncryptionKey' is not valid base64.");
        }

        if (key.Length != KeySizeBytes)
        {
            throw new InvalidOperationException($"'Secrets:EncryptionKey' must decode to exactly {KeySizeBytes} bytes.");
        }

        return new AesGcmSecretProtector(key);
    }

    public string Protect(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var cipherAndTag = new byte[plaintextBytes.Length + TagSizeBytes];

        using var aes = new AesGcm(_key, TagSizeBytes);
        aes.Encrypt(nonce, plaintextBytes, cipherAndTag.AsSpan(0, plaintextBytes.Length), cipherAndTag.AsSpan(plaintextBytes.Length));

        return $"{Version}:{Convert.ToBase64String(nonce)}:{Convert.ToBase64String(cipherAndTag)}";
    }

    public bool TryUnprotect(string protectedValue, out string plaintext)
    {
        plaintext = string.Empty;

        var parts = protectedValue.Split(':');
        if (parts.Length != 3 || parts[0] != Version)
        {
            return false;
        }

        try
        {
            var nonce = Convert.FromBase64String(parts[1]);
            var cipherAndTag = Convert.FromBase64String(parts[2]);
            if (nonce.Length != NonceSizeBytes || cipherAndTag.Length < TagSizeBytes)
            {
                return false;
            }

            var cipherLength = cipherAndTag.Length - TagSizeBytes;
            var plaintextBytes = new byte[cipherLength];

            using var aes = new AesGcm(_key, TagSizeBytes);
            aes.Decrypt(nonce, cipherAndTag.AsSpan(0, cipherLength), cipherAndTag.AsSpan(cipherLength), plaintextBytes);

            plaintext = Encoding.UTF8.GetString(plaintextBytes);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
