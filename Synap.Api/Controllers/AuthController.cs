using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SergioIzq.AspNetCore.Kernel.Controllers;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Application.Features.Users.Commands.Authenticate;
using Synap.Application.Features.Users.Commands.GenerateApiToken;
using Synap.Application.Features.Users.Commands.Register;
using Synap.Application.Features.Users.Queries;

namespace Synap.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : AbsController
{
    public AuthController(ISender sender) : base(sender)
    {
    }

    /// <summary>Registers a new user - open self-registration, no invite code (see specs/identity).</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterUserCommand command)
        => await SendAndHandleAsync(command);

    /// <summary>Authenticates a user and returns a session JWT.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] AuthenticateUserCommand command)
        => await SendAndHandleAsync(command);

    /// <summary>
    /// Generates (or regenerates) the authenticated user's personal access token, used by the
    /// iOS Shortcut for the quick-capture call. The plaintext value is only ever returned here.
    /// </summary>
    [HttpPost("api-token")]
    [Authorize]
    public async Task<IActionResult> GenerateApiToken()
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized(Result.Failure(Error.Unauthorized("Not authenticated.")));
        }

        return await SendAndHandleAsync(new GenerateApiTokenCommand(userId.Value));
    }

    /// <summary>Reports whether the user has an active API token and since when, without revealing its value.</summary>
    [HttpGet("api-token")]
    [Authorize]
    public async Task<IActionResult> GetApiTokenStatus()
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized(Result.Failure(Error.Unauthorized("Not authenticated.")));
        }

        return await SendAndHandleAsync(new GetApiTokenStatusQuery(userId.Value));
    }
}
