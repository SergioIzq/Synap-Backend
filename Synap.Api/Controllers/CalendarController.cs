using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SergioIzq.AspNetCore.Kernel.Controllers;
using Synap.Application.Features.Calendar.Commands;
using Synap.Application.Features.Users;

namespace Synap.Api.Controllers;

/// <summary>
/// La vuelta del consentimiento de Google (calendar-integration design.md Decisión 3). Es anónima
/// porque la redirección del navegador no lleva el JWT: el `state` cifrado dice de quién es.
/// Siempre redirige a Configuración con el resultado, nunca devuelve JSON.
/// </summary>
[ApiController]
[Route("api/calendar")]
public class CalendarController : AbsController
{
    private readonly ISender _sender;
    private readonly AppOptions _appOptions;

    public CalendarController(ISender sender, IOptions<AppOptions> appOptions) : base(sender)
    {
        _sender = sender;
        _appOptions = appOptions.Value;
    }

    [AllowAnonymous]
    [HttpGet("google/callback")]
    public async Task<IActionResult> GoogleCallback(
        [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new CompleteGoogleCalendarCommand(code, state, error), cancellationToken);
        var outcome = result.IsSuccess ? result.Value : GoogleCalendarOutcome.Error;

        return Redirect($"{_appOptions.PublicBaseUrl.TrimEnd('/')}/app/settings?calendar={outcome.ToString().ToLowerInvariant()}");
    }
}
