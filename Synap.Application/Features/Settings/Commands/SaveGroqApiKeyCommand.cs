using Microsoft.Extensions.Options;
using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Settings.Commands;

public sealed record SaveGroqApiKeyCommand(string ApiKey) : ICommand<AiSettingsResponse>;

/// <summary>
/// specs/user-settings "Store a personal Groq API key": validated against Groq first, and only
/// then encrypted and stored - an invalid key, or Groq being unreachable, leaves any previously
/// stored key untouched.
/// </summary>
public sealed class SaveGroqApiKeyCommandHandler : ICommandHandler<SaveGroqApiKeyCommand, AiSettingsResponse>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;
    private readonly ISecretProtector _secretProtector;
    private readonly IAiServiceClient _aiServiceClient;
    private readonly AiOptions _aiOptions;

    public SaveGroqApiKeyCommandHandler(
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

    public async Task<Result<AiSettingsResponse>> Handle(SaveGroqApiKeyCommand request, CancellationToken cancellationToken)
    {
        var apiKey = request.ApiKey?.Trim() ?? string.Empty;
        if (apiKey.Length < 8)
        {
            return Result.Failure<AiSettingsResponse>(apiKey.Length == 0 ? SettingsErrors.GroqKeyRequired : SettingsErrors.GroqKeyInvalid);
        }

        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure<AiSettingsResponse>(SettingsErrors.UserNotFound);
        }

        var validation = await _aiServiceClient.ListModelsAsync(apiKey, cancellationToken);
        if (validation.Status != LlmKeyStatus.Ok)
        {
            return Result.Failure<AiSettingsResponse>(SettingsErrors.FromKeyStatus(validation.Status));
        }

        user.SetGroqApiKey(_secretProtector.Protect(apiKey), apiKey[^4..]);

        // A model chosen for the previous key may not exist for the new one.
        if (user.GroqModel is not null && !validation.Models.Contains(user.GroqModel))
        {
            user.SetGroqModel(null);
        }

        _userWriteRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(AiSettingsResponse.From(user, _aiOptions.DefaultGroqModel));
    }
}
