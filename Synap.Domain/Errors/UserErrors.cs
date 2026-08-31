using SergioIzq.Domain.Kernel.Abstractions.Results;

namespace Synap.Domain.Errors;

public static class UserErrors
{
    public static readonly Error EmailAlreadyRegistered = Error.Validation("This email is already registered.");
    public static readonly Error InvalidCredentials = Error.Unauthorized("Invalid email or password.");
}
