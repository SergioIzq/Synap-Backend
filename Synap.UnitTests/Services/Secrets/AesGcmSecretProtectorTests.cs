using Synap.Infrastructure.Services.Secrets;
using System.Security.Cryptography;

namespace Synap.UnitTests.Services.Secrets;

public class AesGcmSecretProtectorTests
{
    private static AesGcmSecretProtector CreateProtector() => new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Protect_then_unprotect_round_trips()
    {
        var protector = CreateProtector();

        var protectedValue = protector.Protect("gsk_abc123XYZ");

        Assert.True(protector.TryUnprotect(protectedValue, out var plaintext));
        Assert.Equal("gsk_abc123XYZ", plaintext);
    }

    [Fact]
    public void Protected_value_is_versioned_and_does_not_contain_the_plaintext()
    {
        var protectedValue = CreateProtector().Protect("gsk_abc123XYZ");

        Assert.StartsWith("v1:", protectedValue);
        Assert.DoesNotContain("gsk_abc123XYZ", protectedValue);
    }

    [Fact]
    public void Same_plaintext_encrypts_differently_each_time()
    {
        var protector = CreateProtector();

        Assert.NotEqual(protector.Protect("gsk_same"), protector.Protect("gsk_same"));
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected()
    {
        var protector = CreateProtector();
        var parts = protector.Protect("gsk_abc123XYZ").Split(':');
        var cipher = Convert.FromBase64String(parts[2]);
        cipher[0] ^= 0xFF;

        var tampered = $"{parts[0]}:{parts[1]}:{Convert.ToBase64String(cipher)}";

        Assert.False(protector.TryUnprotect(tampered, out _));
    }

    [Fact]
    public void Value_encrypted_with_another_master_key_is_rejected()
    {
        var protectedValue = CreateProtector().Protect("gsk_abc123XYZ");

        Assert.False(CreateProtector().TryUnprotect(protectedValue, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-protected-value")]
    [InlineData("v2:AAAA:BBBB")]
    [InlineData("v1:!!!:???")]
    public void Malformed_values_are_rejected_without_throwing(string value)
    {
        Assert.False(CreateProtector().TryUnprotect(value, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 at all")]
    [InlineData("c2hvcnQ=")] // valid base64, but only 5 bytes
    public void FromBase64_fails_fast_on_an_invalid_master_key(string? key)
    {
        Assert.Throws<InvalidOperationException>(() => AesGcmSecretProtector.FromBase64(key));
    }

    [Fact]
    public void FromBase64_accepts_a_32_byte_key()
    {
        var protector = AesGcmSecretProtector.FromBase64(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        Assert.True(protector.TryUnprotect(protector.Protect("x"), out var plaintext));
        Assert.Equal("x", plaintext);
    }
}
