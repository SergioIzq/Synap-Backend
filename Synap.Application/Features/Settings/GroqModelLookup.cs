using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Settings;

/// <summary>Shared by the model list query and the model selection command, which validates against it.</summary>
internal static class GroqModelLookup
{
    public static async Task<Result<IReadOnlyList<LlmModel>>> ListForUserAsync(
        User user, ISecretProtector secretProtector, IAiServiceClient aiServiceClient, CancellationToken cancellationToken)
    {
        if (!user.HasGroqApiKey)
        {
            return Result.Failure<IReadOnlyList<LlmModel>>(SettingsErrors.GroqKeyNotConfigured);
        }

        if (!secretProtector.TryUnprotect(user.GroqApiKeyEncrypted!, out var apiKey))
        {
            return Result.Failure<IReadOnlyList<LlmModel>>(SettingsErrors.GroqKeyUnreadable);
        }

        var result = await aiServiceClient.ListModelsAsync(apiKey, cancellationToken);

        return result.Status == LlmKeyStatus.Ok
            ? Result.Success(result.Models)
            : Result.Failure<IReadOnlyList<LlmModel>>(SettingsErrors.FromKeyStatus(result.Status));
    }
}
