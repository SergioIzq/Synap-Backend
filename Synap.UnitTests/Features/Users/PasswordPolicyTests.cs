using Synap.Application.Features.Users;

namespace Synap.UnitTests.Features.Users;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData("contraseña larga y segura", true)]
    public void Validates_length(string? password, bool valid)
    {
        Assert.Equal(valid, PasswordPolicy.Validate(password).IsSuccess);
    }

    [Fact]
    public void Upper_bound_is_inclusive()
    {
        Assert.True(PasswordPolicy.Validate(new string('a', PasswordPolicy.MaxLength)).IsSuccess);
        Assert.False(PasswordPolicy.Validate(new string('a', PasswordPolicy.MaxLength + 1)).IsSuccess);
    }
}
