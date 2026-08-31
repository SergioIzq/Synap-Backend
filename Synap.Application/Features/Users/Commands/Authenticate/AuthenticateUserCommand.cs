using SergioIzq.Application.Kernel.Messaging;

namespace Synap.Application.Features.Users.Commands.Authenticate;

public sealed record AuthenticateUserCommand(
    string Email,
    string Password
) : ICommand<AuthenticateUserResponse>;

public sealed record AuthenticateUserResponse(
    string Token,
    DateTime ExpiresAt
);
