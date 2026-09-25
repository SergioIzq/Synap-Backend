using SergioIzq.Domain.Kernel.Abstractions.Results;

namespace Synap.Domain.Errors;

public static class UserErrors
{
    public static readonly Error EmailAlreadyRegistered = Error.Validation("Este correo ya está registrado.");
    public static readonly Error InvalidCredentials = Error.Unauthorized("Correo o contraseña incorrectos.");
}
