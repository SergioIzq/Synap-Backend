using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SergioIzq.AspNetCore.Kernel.Controllers;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Application.Features.Users.Commands.Authenticate;
using Synap.Application.Features.Users.Commands.ForgotPassword;
using Synap.Application.Features.Users.Commands.GenerateApiToken;
using Synap.Application.Features.Users.Commands.ResetPassword;
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
    [EnableRateLimiting(RateLimitPolicies.Register)]
    public async Task<IActionResult> Register([FromBody] RegisterUserCommand command)
        => await SendAndHandleAsync(command);

    /// <summary>Authenticates a user and returns a session JWT.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> Login([FromBody] AuthenticateUserCommand command)
        => await SendAndHandleAsync(command);

    /// <summary>
    /// Emails a password-recovery link if the address has an account. Always 200 with the same
    /// body - it never reveals whether the email is registered (specs/identity).
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PasswordRecovery)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordCommand command)
        => await SendAndHandleAsync(command);

    /// <summary>Sets a new password with the emailed token; ends every existing session.</summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordCommand command)
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
            return Unauthorized(Result.Failure(Error.Unauthorized("No autenticado.")));
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
            return Unauthorized(Result.Failure(Error.Unauthorized("No autenticado.")));
        }

        return await SendAndHandleAsync(new GetApiTokenStatusQuery(userId.Value));
    }
}
