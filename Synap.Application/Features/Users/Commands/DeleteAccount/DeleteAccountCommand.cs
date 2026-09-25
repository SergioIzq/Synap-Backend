using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Domain.Errors;
using Synap.Shared.Application;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Users.Commands.DeleteAccount;

/// <summary>specs/identity "Delete account" - irreversible, confirmed with the password.</summary>
public sealed record DeleteAccountCommand(string Password) : ICommand;

public sealed class DeleteAccountCommandHandler : ICommandHandler<DeleteAccountCommand>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUserContext _userContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IUserExistenceCache _userExistenceCache;

    public DeleteAccountCommandHandler(
        IUserWriteRepository userWriteRepository,
        IUserContext userContext,
        IPasswordHasher passwordHasher,
        IUserExistenceCache userExistenceCache)
    {
        _userWriteRepository = userWriteRepository;
        _userContext = userContext;
        _passwordHasher = passwordHasher;
        _userExistenceCache = userExistenceCache;
    }

    public async Task<Result> Handle(DeleteAccountCommand request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        var user = await _userWriteRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(UserErrors.NotFound);
        }

        if (string.IsNullOrEmpty(request.Password) || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash.Value))
        {
            return Result.Failure(UserErrors.WrongPassword);
        }

        await _userWriteRepository.DeleteWithAllDataAsync(userId, cancellationToken);

        // The session JWT is checked against this cache - drop the entry so it stops working now.
        _userExistenceCache.Invalidate(userId);

        return Result.Success();
    }
}
