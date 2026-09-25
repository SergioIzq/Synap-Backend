using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Domain.Errors;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects;

namespace Synap.Application.Features.Users.Commands.ResetPassword;

/// <summary>
/// specs/identity "Password recovery by email": the emailed token (looked up by its hash) sets
/// a new password once, within its hour, and ends every existing session. Unknown, used and
/// expired tokens all get the same error.
/// </summary>
public sealed record ResetPasswordCommand(string Token, string NewPassword) : ICommand;

public sealed class ResetPasswordCommandHandler : ICommandHandler<ResetPasswordCommand>
{
    private readonly IUserReadRepository _userReadRepository;
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IApiTokenHasher _tokenHasher;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IUserSessionCache _userSessionCache;

    public ResetPasswordCommandHandler(
        IUserReadRepository userReadRepository,
        IUserWriteRepository userWriteRepository,
        IUnitOfWork unitOfWork,
        IApiTokenHasher tokenHasher,
        IPasswordHasher passwordHasher,
        IUserSessionCache userSessionCache)
    {
        _userReadRepository = userReadRepository;
        _userWriteRepository = userWriteRepository;
        _unitOfWork = unitOfWork;
        _tokenHasher = tokenHasher;
        _passwordHasher = passwordHasher;
        _userSessionCache = userSessionCache;
    }

    public async Task<Result> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return Result.Failure(UserErrors.InvalidResetLink);
        }

        var found = await _userReadRepository.GetByPasswordResetTokenHashAsync(_tokenHasher.Hash(request.Token.Trim()), cancellationToken);
        var user = found is null ? null : await _userWriteRepository.GetByIdAsync(found.Id.Value, cancellationToken);
        if (user is null)
        {
            return Result.Failure(UserErrors.InvalidResetLink);
        }

        if (!user.HasValidPasswordReset(DateTime.UtcNow))
        {
            // Expired: drop it so the hash can't linger in the table.
            user.ClearPasswordReset();
            _userWriteRepository.Update(user);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Failure(UserErrors.InvalidResetLink);
        }

        // An invalid password keeps the link usable, so the user can simply try again.
        var policy = PasswordPolicy.Validate(request.NewPassword);
        if (policy.IsFailure)
        {
            return policy;
        }

        var hash = PasswordHash.Create(_passwordHasher.HashPassword(request.NewPassword));
        if (hash.IsFailure)
        {
            return Result.Failure(hash.Error);
        }

        user.CompletePasswordReset(hash.Value);
        _userWriteRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _userSessionCache.Invalidate(user.Id.Value);

        return Result.Success();
    }
}
