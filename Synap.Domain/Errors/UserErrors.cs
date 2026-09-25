using SergioIzq.Domain.Kernel.Abstractions.Results;

namespace Synap.Domain.Errors;

public static class UserErrors
{
    public static readonly Error EmailAlreadyRegistered = Error.Validation("Este correo ya está registrado.");
    public static readonly Error InvalidCredentials = Error.Unauthorized("Correo o contraseña incorrectos.");

    // Validation, not Unauthorized: a 401 would make the web app's error interceptor end the
    // session, when the user merely mistyped their current password.
    public static readonly Error WrongCurrentPassword = Error.Validation("La contraseña actual no es correcta.");
    public static readonly Error WrongPassword = Error.Validation("La contraseña no es correcta.");
    public static readonly Error NotFound = Error.NotFound("Usuario no encontrado.");
}
