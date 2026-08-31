using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Domain.Errors;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects;

namespace Synap.Application.Features.Users.Commands.Authenticate;

public sealed class AuthenticateUserCommandHandler : ICommandHandler<AuthenticateUserCommand, AuthenticateUserResponse>
{
    private readonly IUserReadRepository _userReadRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;

    public AuthenticateUserCommandHandler(
        IUserReadRepository userReadRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator)
    {
        _userReadRepository = userReadRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenGenerator = jwtTokenGenerator;
    }

    public async Task<Result<AuthenticateUserResponse>> Handle(AuthenticateUserCommand request, CancellationToken cancellationToken)
    {
        var emailResult = Email.Create(request.Email);
        if (emailResult.IsFailure)
        {
            return Result.Failure<AuthenticateUserResponse>(UserErrors.InvalidCredentials);
        }

        var user = await _userReadRepository.GetByEmailAsync(emailResult.Value, cancellationToken);
        if (user is null || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash.Value))
        {
            // Same error whether the email doesn't exist or the password is wrong - avoids
            // revealing which one was incorrect.
            return Result.Failure<AuthenticateUserResponse>(UserErrors.InvalidCredentials);
        }

        var (token, expiresAt) = _jwtTokenGenerator.GenerateToken(user);

        return Result.Success(new AuthenticateUserResponse(token, expiresAt));
    }
}
