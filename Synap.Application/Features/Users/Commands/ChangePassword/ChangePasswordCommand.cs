using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Domain.Errors;
using Synap.Shared.Application;
using Synap.Application.Features.Users.Commands.Authenticate;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects;

namespace Synap.Application.Features.Users.Commands.ChangePassword;

/// <summary>
/// specs/identity "Change password" and "Password changes end other sessions": the security
/// stamp rotates, so every other session stops working, and the caller gets a fresh token so
/// the session it used to make the change carries on.
/// </summary>
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword) : ICommand<AuthenticateUserResponse>;

public sealed class ChangePasswordCommandHandler : ICommandHandler<ChangePasswordCommand, AuthenticateUserResponse>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly IUserSessionCache _userSessionCache;

    public ChangePasswordCommandHandler(
        IUserWriteRepository userWriteRepository,
        IUnitOfWork unitOfWork,
        IUserContext userContext,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator,
        IUserSessionCache userSessionCache)
    {
        _jwtTokenGenerator = jwtTokenGenerator;
        _userSessionCache = userSessionCache;
        _userWriteRepository = userWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
        _passwordHasher = passwordHasher;
    }

    public async Task<Result<AuthenticateUserResponse>> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure<AuthenticateUserResponse>(UserErrors.NotFound);
        }

        if (string.IsNullOrEmpty(request.CurrentPassword) || !_passwordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash.Value))
        {
            return Result.Failure<AuthenticateUserResponse>(UserErrors.WrongCurrentPassword);
        }

        var policy = PasswordPolicy.Validate(request.NewPassword);
        if (policy.IsFailure)
        {
            return Result.Failure<AuthenticateUserResponse>(policy.Error);
        }

        var hashResult = PasswordHash.Create(_passwordHasher.HashPassword(request.NewPassword));
        if (hashResult.IsFailure)
        {
            return Result.Failure<AuthenticateUserResponse>(hashResult.Error);
        }

        user.ChangePassword(hashResult.Value);
        _userWriteRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _userSessionCache.Invalidate(user.Id.Value);

        var (token, expiresAt) = _jwtTokenGenerator.GenerateToken(user);
        return Result.Success(new AuthenticateUserResponse(token, expiresAt));
    }
}
