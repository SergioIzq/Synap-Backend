using SergioIzq.Domain.Kernel.Abstractions;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Shared.Domain.ValueObjects.Ids;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;

namespace Synap.Domain;

/// <summary>
/// Uno de los calendarios de eventos de la conexión CalDAV de un usuario (calendar-store-and-view
/// design.md Decisión 2). El nombre y el color del servidor se actualizan en cada sincronización;
/// el ámbito, el color propio y si está activo son preferencias del usuario que solo viven en
/// Synap y se conservan mientras el calendario exista en el servidor.
///
/// <see cref="LastSyncError"/> es una categoría (<see cref="CalendarSyncErrors"/>), nunca el
/// contenido de una respuesta.
/// </summary>
[Table("calendars")]
public sealed partial class Calendar : AbsEntity<CalendarId>
{
    public const int MaxHrefLength = 2048;
    public const int MaxDisplayNameLength = 200;

    public static readonly Error NotFound = Error.NotFound("Calendario no encontrado.");
    public static readonly Error InvalidColor = Error.Validation("El color tiene que tener la forma #RRGGBB.");
    public static readonly Error InvalidScope = Error.Validation("El ámbito tiene que ser «personal» o «work».");

    private Calendar() : base(CalendarId.Create(Guid.NewGuid()).Value)
    {
    }

    private Calendar(CalendarId id, UserId userId, string href, string displayName, string? serverColor) : base(id)
    {
        UserId = userId;
        Href = href;
        DisplayName = displayName;
        ServerColor = serverColor;
        Scope = CalendarScope.Personal;
        Enabled = true;
    }

    public UserId UserId { get; private set; }

    /// <summary>La URL absoluta de la colección en el servidor; única por usuario.</summary>
    public string Href { get; private set; } = null!;

    public string DisplayName { get; private set; } = null!;

    /// <summary>El color que da el servidor (`calendar-color`), normalizado a #RRGGBB.</summary>
    public string? ServerColor { get; private set; }

    /// <summary>El color que eligió el usuario; null es usar el del servidor o el de la paleta.</summary>
    public string? Color { get; private set; }

    public CalendarScope Scope { get; private set; }
    public bool Enabled { get; private set; }

    public string? SyncToken { get; private set; }
    public string? CTag { get; private set; }
    public DateTime? LastSyncedAt { get; private set; }
    public string? LastSyncError { get; private set; }

    /// <summary>El color con el que se muestra: el propio, el del servidor o uno de la paleta.</summary>
    public string EffectiveColor => Color ?? ServerColor ?? CalendarPalette.For(Href);

    /// <summary>Un calendario nuevo empieza activo y Personal (specs/calendar-integration).</summary>
    public static Calendar Create(UserId userId, string href, string? displayName, string? serverColor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(href);

        return new Calendar(
            CalendarId.Create(Guid.NewGuid()).Value, userId, href.Trim(), CleanName(displayName), NormalizeColor(serverColor));
    }

    /// <summary>Lo que el servidor puede cambiar. Las preferencias del usuario no se tocan.</summary>
    public void UpdateFromServer(string? displayName, string? serverColor)
    {
        DisplayName = CleanName(displayName);
        ServerColor = NormalizeColor(serverColor);
    }

    public void ChangeScope(CalendarScope scope) => Scope = scope;

    /// <summary>Null o vacío vuelve al color del servidor.</summary>
    public Result ChangeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            Color = null;
            return Result.Success();
        }

        if (!HexColor().IsMatch(color.Trim()))
        {
            return Result.Failure(InvalidColor);
        }

        Color = color.Trim().ToLowerInvariant();
        return Result.Success();
    }

    public void Enable() => Enabled = true;

    /// <summary>
    /// Deja de sincronizarse y olvida su punto de partida, para que activarlo de nuevo lo copie
    /// desde cero (specs/calendar-integration "Calendario desactivado"). Los eventos los borra
    /// quien lo desactiva.
    /// </summary>
    public void Disable()
    {
        Enabled = false;
        ResetSyncState();
    }

    public void ResetSyncState()
    {
        SyncToken = null;
        CTag = null;
    }

    /// <summary>Una sincronización correcta: el nuevo punto de partida y la hora.</summary>
    public void RecordSynced(string? syncToken, string? ctag, DateTime nowUtc)
    {
        SyncToken = string.IsNullOrWhiteSpace(syncToken) ? null : syncToken;
        CTag = string.IsNullOrWhiteSpace(ctag) ? null : ctag;
        LastSyncedAt = nowUtc;
        LastSyncError = null;
    }

    /// <summary>Un fallo deja la copia y la última hora correcta como estaban.</summary>
    public void RecordSyncFailed(string category) => LastSyncError = category;

    /// <summary>
    /// Apple manda `calendar-color` como #RRGGBBAA; se guarda #RRGGBB. Lo que no es un color se
    /// ignora: el servidor no decide qué se pinta.
    /// </summary>
    public static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        var trimmed = color.Trim();
        if (ServerHexColor().IsMatch(trimmed))
        {
            return trimmed[..7].ToLowerInvariant();
        }

        return null;
    }

    private static string CleanName(string? displayName)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? "Calendario" : displayName.Trim();
        return name.Length <= MaxDisplayNameLength ? name : name[..MaxDisplayNameLength];
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex("^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")]
    private static partial Regex ServerHexColor();
}

/// <summary>A qué parte de la vida pertenece un calendario (specs/calendar-integration).</summary>
public enum CalendarScope
{
    Personal,
    Work,
}

/// <summary>El ámbito tal como lo escriben la API, el asistente y la base de datos.</summary>
public static class CalendarScopes
{
    public static string ToText(CalendarScope scope) => scope == CalendarScope.Work ? "work" : "personal";

    public static CalendarScope? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "personal" => CalendarScope.Personal,
        "work" => CalendarScope.Work,
        _ => null,
    };
}

/// <summary>Los colores para un calendario que no trae ninguno: siempre el mismo para la misma URL.</summary>
public static class CalendarPalette
{
    private static readonly string[] Colors =
        ["#3b82f6", "#10b981", "#f59e0b", "#ef4444", "#8b5cf6", "#ec4899", "#14b8a6", "#64748b"];

    public static string For(string href)
    {
        // Un hash estable entre procesos; string.GetHashCode cambia en cada arranque.
        var hash = 0u;
        foreach (var character in href)
        {
            hash = unchecked(hash * 31 + character);
        }

        return Colors[hash % (uint)Colors.Length];
    }
}

/// <summary>Las categorías de fallo de sincronización que se guardan; nunca el contenido.</summary>
public static class CalendarSyncErrors
{
    public const string Unreachable = "unreachable";
    public const string ServerError = "server_error";
    public const string InvalidResponse = "invalid_response";
}
