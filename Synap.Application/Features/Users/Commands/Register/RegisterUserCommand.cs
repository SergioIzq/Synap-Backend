using SergioIzq.Application.Kernel.Messaging;

namespace Synap.Application.Features.Users.Commands.Register;

public sealed record RegisterUserCommand(
    string Email,
    string Password
) : ICommand;
