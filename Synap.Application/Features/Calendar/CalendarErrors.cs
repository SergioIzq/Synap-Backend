using SergioIzq.Domain.Kernel.Abstractions.Results;

namespace Synap.Application.Features.Calendar;

/// <summary>
/// Lo que se le dice al usuario sobre su calendario, en español. Distingue lo que la spec pide
/// distinguir al conectar un CalDAV: URL sin HTTPS, credenciales rechazadas, servidor inalcanzable
/// y sin calendario utilizable (specs/calendar-integration "Conectar un calendario CalDAV").
/// </summary>
public static class CalendarErrors
{
    public static readonly Error UserNotFound = Error.NotFound("Usuario no encontrado.");

    public static readonly Error ServerUrlInvalid =
        Error.Validation("Indica una dirección válida del servidor CalDAV o de tu calendario.");

    public static readonly Error HttpsRequired =
        Error.Validation("La dirección del servidor CalDAV tiene que empezar por https://.");

    public static readonly Error CredentialsRequired =
        Error.Validation("Indica el usuario y la contraseña del servidor CalDAV.");

    public static readonly Error CredentialsRejected =
        Error.Validation("El servidor CalDAV ha rechazado el usuario o la contraseña. Revísalos; si tu proveedor lo pide, usa una contraseña de aplicación.");

    public static readonly Error Unreachable =
        Error.Validation("No se ha podido contactar con el servidor CalDAV. Revisa la dirección o inténtalo más tarde.");

    public static readonly Error NoCalendar =
        Error.Validation("Las credenciales son válidas, pero no se ha encontrado ningún calendario de eventos en esa cuenta.");
}
