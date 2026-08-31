using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Users.Commands.GenerateApiToken;

public sealed class GenerateApiTokenCommandHandler : ICommandHandler<GenerateApiTokenCommand, string>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IApiTokenHasher _apiTokenHasher;

    public GenerateApiTokenCommandHandler(
        IUserWriteRepository userWriteRepository,
        IUnitOfWork unitOfWork,
        IApiTokenHasher apiTokenHasher)
    {
        _userWriteRepository = userWriteRepository;
        _unitOfWork = unitOfWork;
        _apiTokenHasher = apiTokenHasher;
    }

    public async Task<Result<string>> Handle(GenerateApiTokenCommand request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<string>(Error.NotFound("User not found."));
        }

        var (plainToken, hash) = _apiTokenHasher.GenerateToken();

        user.SetApiToken(hash);
        _userWriteRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(plainToken);
    }
}
