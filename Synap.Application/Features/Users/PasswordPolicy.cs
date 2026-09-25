using SergioIzq.Domain.Kernel.Abstractions.Results;

namespace Synap.Application.Features.Users;

/// <summary>
/// The one password rule set, shared by registration and password change (specs/identity
/// "Change password": "the same password rules as registration"). Mirrors the register form's
/// minLength(8); the upper bound keeps hashing cost bounded.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    public static readonly Error Invalid =
        Error.Validation($"La contraseña debe tener entre {MinLength} y {MaxLength} caracteres.");

    public static Result Validate(string? password)
        => password is { Length: >= MinLength and <= MaxLength }
            ? Result.Success()
            : Result.Failure(Invalid);
}
