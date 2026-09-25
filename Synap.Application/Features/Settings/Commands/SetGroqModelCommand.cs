using Microsoft.Extensions.Options;
using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Settings.Commands;

/// <summary>Null/empty Model resets to the server's default model.</summary>
public sealed record SetGroqModelCommand(string? Model) : ICommand<AiSettingsResponse>;

public sealed class SetGroqModelCommandHandler : ICommandHandler<SetGroqModelCommand, AiSettingsResponse>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;
    private readonly ISecretProtector _secretProtector;
    private readonly IAiServiceClient _aiServiceClient;
    private readonly AiOptions _aiOptions;

    public SetGroqModelCommandHandler(
        IUserWriteRepository userWriteRepository,
        IUnitOfWork unitOfWork,
        IUserContext userContext,
        ISecretProtector secretProtector,
        IAiServiceClient aiServiceClient,
        IOptions<AiOptions> aiOptions)
    {
        _userWriteRepository = userWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
        _secretProtector = secretProtector;
        _aiServiceClient = aiServiceClient;
        _aiOptions = aiOptions.Value;
    }

    public async Task<Result<AiSettingsResponse>> Handle(SetGroqModelCommand request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure<AiSettingsResponse>(SettingsErrors.UserNotFound);
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim();

        if (model is not null)
        {
            var available = await GroqModelLookup.ListForUserAsync(user, _secretProtector, _aiServiceClient, cancellationToken);
            if (available.IsFailure)
            {
                return Result.Failure<AiSettingsResponse>(available.Error);
            }

            if (!available.Value.Contains(model))
            {
                return Result.Failure<AiSettingsResponse>(SettingsErrors.GroqModelUnknown);
            }
        }

        user.SetGroqModel(model);
        _userWriteRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(AiSettingsResponse.From(user, _aiOptions.DefaultGroqModel));
    }
}
