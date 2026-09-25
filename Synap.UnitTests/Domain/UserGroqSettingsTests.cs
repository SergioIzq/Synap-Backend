using Synap.Domain;
using Synap.Shared.Domain.ValueObjects;

namespace Synap.UnitTests.Domain;

public class UserGroqSettingsTests
{
    internal static User NewUser()
        => User.Create(Email.CreateFromDatabase("user@example.com"), PasswordHash.CreateFromDatabase("hash"));

    [Fact]
    public void New_user_has_no_groq_key()
    {
        var user = NewUser();

        Assert.False(user.HasGroqApiKey);
        Assert.Null(user.GroqModel);
    }

    [Fact]
    public void SetGroqApiKey_stores_encrypted_value_last4_and_timestamp()
    {
        var user = NewUser();

        user.SetGroqApiKey("v1:nonce:cipher", "a1B2");

        Assert.True(user.HasGroqApiKey);
        Assert.Equal("v1:nonce:cipher", user.GroqApiKeyEncrypted);
        Assert.Equal("a1B2", user.GroqApiKeyLast4);
        Assert.NotNull(user.GroqApiKeyUpdatedAt);
    }

    [Fact]
    public void ClearGroqApiKey_removes_key_and_model()
    {
        var user = NewUser();
        user.SetGroqApiKey("v1:nonce:cipher", "a1B2");
        user.SetGroqModel("llama");

        user.ClearGroqApiKey();

        Assert.False(user.HasGroqApiKey);
        Assert.Null(user.GroqApiKeyLast4);
        Assert.Null(user.GroqApiKeyUpdatedAt);
        Assert.Null(user.GroqModel);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(" llama ", "llama")]
    public void SetGroqModel_normalises_blank_to_default(string? input, string? expected)
    {
        var user = NewUser();

        user.SetGroqModel(input);

        Assert.Equal(expected, user.GroqModel);
    }
}
