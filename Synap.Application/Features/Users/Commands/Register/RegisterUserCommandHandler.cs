using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Domain.Errors;
using Synap.Shared.Domain.ValueObjects;

namespace Synap.Application.Features.Users.Commands.Register;

public sealed class RegisterUserCommandHandler : ICommandHandler<RegisterUserCommand>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUserReadRepository _userReadRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;

    public RegisterUserCommandHandler(
        IUserWriteRepository userWriteRepository,
        IUserReadRepository userReadRepository,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher)
    {
        _userWriteRepository = userWriteRepository;
        _userReadRepository = userReadRepository;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
    }

    public async Task<Result> Handle(RegisterUserCommand request, CancellationToken cancellationToken)
    {
        var emailResult = Email.Create(request.Email);
        if (emailResult.IsFailure)
        {
            return Result.Failure(emailResult.Error);
        }

        var existingUser = await _userReadRepository.GetByEmailAsync(emailResult.Value, cancellationToken);
        if (existingUser is not null)
        {
            return Result.Failure(UserErrors.EmailAlreadyRegistered);
        }

        var hashedPassword = _passwordHasher.HashPassword(request.Password);
        var passwordHashResult = PasswordHash.Create(hashedPassword);
        if (passwordHashResult.IsFailure)
        {
            return Result.Failure(passwordHashResult.Error);
        }

        var user = User.Create(emailResult.Value, passwordHashResult.Value);

        await _userWriteRepository.CreateAsync(user, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
