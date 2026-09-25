using MediatR;
using Microsoft.AspNetCore.Mvc;
using SergioIzq.AspNetCore.Kernel.Controllers;
using Synap.Application.Features.Users.Commands.ChangePassword;
using Synap.Application.Features.Users.Commands.DeleteAccount;
using Synap.Application.Features.Users.Queries;

namespace Synap.Api.Controllers;

/// <summary>
/// The signed-in user's own account (specs/identity). Kept apart from AuthController, which
/// only issues credentials. Authenticated via the global FallbackPolicy.
/// </summary>
[ApiController]
[Route("api/users/me")]
public class UsersController : AbsController
{
    public UsersController(ISender sender) : base(sender)
    {
    }

    [HttpGet]
    public async Task<IActionResult> Get()
        => await SendAndHandleAsync(new GetCurrentUserQuery());

    [HttpPut("password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordCommand command)
        => await SendAndHandleAsync(command);

    [HttpDelete]
    public async Task<IActionResult> Delete([FromBody] DeleteAccountCommand command)
        => await SendAndHandleAsync(command);
}
