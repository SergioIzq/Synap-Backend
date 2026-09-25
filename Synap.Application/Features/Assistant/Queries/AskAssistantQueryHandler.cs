using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Assistant.Queries;

public sealed class AskAssistantQueryHandler : IQueryHandler<AskAssistantQuery, AssistantAnswer>
{
    private readonly IAiServiceClient _aiServiceClient;
    private readonly IUserContext _userContext;
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly ISecretProtector _secretProtector;

    public AskAssistantQueryHandler(
        IAiServiceClient aiServiceClient,
        IUserContext userContext,
        IUserWriteRepository userWriteRepository,
        ISecretProtector secretProtector)
    {
        _aiServiceClient = aiServiceClient;
        _userContext = userContext;
        _userWriteRepository = userWriteRepository;
        _secretProtector = secretProtector;
    }

    public async Task<Result<AssistantAnswer>> Handle(AskAssistantQuery request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        var user = await _userWriteRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<AssistantAnswer>(Error.NotFound("Usuario no encontrado."));
        }

        // No shared fallback key (specs/ai-assistant "Answers generated with the user's own
        // credentials"): without the user's own key the provider is never contacted.
        if (!user.HasGroqApiKey)
        {
            return Result.Success(AssistantAnswer.Failed(AssistantAnswer.KeyMissingMessage, AssistantAnswerStatus.KeyMissing));
        }

        // A key that no longer decrypts (e.g. the master key was lost/rotated) is treated like
        // one the provider rejected: the fix for the user is the same - re-enter it.
        if (!_secretProtector.TryUnprotect(user.GroqApiKeyEncrypted!, out var groqApiKey))
        {
            return Result.Success(AssistantAnswer.Failed(AssistantAnswer.InvalidKeyMessage, AssistantAnswerStatus.InvalidKey));
        }

        var answer = await _aiServiceClient.AskAsync(userId, request.Question, groqApiKey, user.GroqModel, cancellationToken);
        return Result.Success(answer);
    }
}
