using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Domain.Errors;
using Synap.Shared.Application;
using Synap.Shared.Domain.ValueObjects;

namespace Synap.Application.Features.Users.Commands.ChangePassword;

/// <summary>
/// specs/identity "Change password". Other sessions keep working until their JWT expires
/// (backend-hardening design.md, Non-Goals).
/// </summary>
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword) : ICommand;

public sealed class ChangePasswordCommandHandler : ICommandHandler<ChangePasswordCommand>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;
    private readonly IPasswordHasher _passwordHasher;

    public ChangePasswordCommandHandler(
        IUserWriteRepository userWriteRepository,
        IUnitOfWork unitOfWork,
        IUserContext userContext,
        IPasswordHasher passwordHasher)
    {
        _userWriteRepository = userWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
        _passwordHasher = passwordHasher;
    }

    public async Task<Result> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure(UserErrors.NotFound);
        }

        if (string.IsNullOrEmpty(request.CurrentPassword) || !_passwordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash.Value))
        {
            return Result.Failure(UserErrors.WrongCurrentPassword);
        }

        var policy = PasswordPolicy.Validate(request.NewPassword);
        if (policy.IsFailure)
        {
            return policy;
        }

        var hashResult = PasswordHash.Create(_passwordHasher.HashPassword(request.NewPassword));
        if (hashResult.IsFailure)
        {
            return Result.Failure(hashResult.Error);
        }

        user.ChangePassword(hashResult.Value);
        _userWriteRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
