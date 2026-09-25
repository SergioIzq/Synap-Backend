using Microsoft.Extensions.Options;
using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Settings.Commands;

public sealed record DeleteGroqApiKeyCommand : ICommand<AiSettingsResponse>;

public sealed class DeleteGroqApiKeyCommandHandler : ICommandHandler<DeleteGroqApiKeyCommand, AiSettingsResponse>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;
    private readonly AiOptions _aiOptions;

    public DeleteGroqApiKeyCommandHandler(
        IUserWriteRepository userWriteRepository,
        IUnitOfWork unitOfWork,
        IUserContext userContext,
        IOptions<AiOptions> aiOptions)
    {
        _userWriteRepository = userWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
        _aiOptions = aiOptions.Value;
    }

    public async Task<Result<AiSettingsResponse>> Handle(DeleteGroqApiKeyCommand request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure<AiSettingsResponse>(SettingsErrors.UserNotFound);
        }

        user.ClearGroqApiKey();
        _userWriteRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(AiSettingsResponse.From(user, _aiOptions.DefaultGroqModel));
    }
}
