using MediatR;
using Microsoft.AspNetCore.Mvc;
using SergioIzq.AspNetCore.Kernel.Controllers;
using Synap.Application.Features.Calendar;
using Synap.Application.Features.Calendar.Commands;
using Synap.Application.Features.Calendar.Queries;

namespace Synap.Api.Controllers;

/// <summary>
/// Los calendarios copiados del usuario y sus eventos (calendar-store-and-view design.md Decisión 6).
/// Solo devuelve la copia; la sincronización con el servidor es asíncrona y se puede pedir aquí.
/// Autenticado por la política global FallbackPolicy como el resto de controladores.
/// </summary>
[ApiController]
[Route("api/calendar")]
public class CalendarController : AbsController
{
    private readonly IManualSyncRateLimit _rateLimit;

    public CalendarController(ISender sender, IManualSyncRateLimit rateLimit) : base(sender)
    {
        _rateLimit = rateLimit;
    }

    /// <summary>Los calendarios del usuario: nombre, color, ámbito, activo y estado de sync.</summary>
    [HttpGet("calendars")]
    public async Task<IActionResult> GetCalendars()
        => await SendAndHandleAsync(new GetCalendarsQuery());

    /// <summary>
    /// Cambia ámbito, color y/o si está activo de un calendario. Un id ajeno devuelve 404.
    /// Para resetear el color al del servidor envía <c>color: ""</c>.
    /// </summary>
    [HttpPatch("calendars/{id:guid}")]
    public async Task<IActionResult> PatchCalendar(Guid id, [FromBody] PatchCalendarRequest request)
        => await SendAndHandleAsync(new PatchCalendarCommand(id, request.Scope, request.Color, request.Enabled));

    /// <summary>
    /// Los eventos de la copia en el intervalo indicado (máx. 62 días, UTC).
    /// <c>scope</c> = "personal" | "work"; si se omite, devuelve todos los activos.
    /// </summary>
    [HttpGet("events")]
    public async Task<IActionResult> GetEvents(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] string? scope = null)
        => await SendAndHandleAsync(new GetCalendarEventsQuery(
            DateTime.SpecifyKind(from, DateTimeKind.Utc),
            DateTime.SpecifyKind(to, DateTimeKind.Utc),
            scope));

    /// <summary>
    /// Pide una sincronización inmediata. Devuelve 429 con mensaje si se pide antes de 1 minuto
    /// de la petición anterior (design.md Decisión 6).
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> RequestSync()
    {
        if (RequireCurrentUserId(out var userId) is { } unauthorized)
        {
            return unauthorized;
        }

        var waitSeconds = _rateLimit.TryRequest(userId);
        if (waitSeconds > 0)
        {
            Response.Headers["Retry-After"] = waitSeconds.ToString();
            return StatusCode(429, new { message = $"Espera {waitSeconds} segundo{(waitSeconds == 1 ? "" : "s")} antes de volver a sincronizar." });
        }

        return await SendAndHandleAsync(new EnqueueCalendarSyncCommand());
    }

    public sealed record PatchCalendarRequest(string? Scope, string? Color, bool? Enabled);
}
