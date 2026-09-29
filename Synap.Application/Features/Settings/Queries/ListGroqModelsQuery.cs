using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Settings.Queries;

/// <summary>
/// Chat models available to the user's stored key, each marked as supporting assistant actions
/// or not (specs/user-settings "Choose the assistant model").
/// </summary>
public sealed record ListGroqModelsQuery : IQuery<IReadOnlyList<LlmModel>>;

public sealed class ListGroqModelsQueryHandler : IQueryHandler<ListGroqModelsQuery, IReadOnlyList<LlmModel>>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUserContext _userContext;
    private readonly ISecretProtector _secretProtector;
    private readonly IAiServiceClient _aiServiceClient;

    public ListGroqModelsQueryHandler(
        IUserWriteRepository userWriteRepository,
        IUserContext userContext,
        ISecretProtector secretProtector,
        IAiServiceClient aiServiceClient)
    {
        _userWriteRepository = userWriteRepository;
        _userContext = userContext;
        _secretProtector = secretProtector;
        _aiServiceClient = aiServiceClient;
    }

    public async Task<Result<IReadOnlyList<LlmModel>>> Handle(ListGroqModelsQuery request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure<IReadOnlyList<LlmModel>>(SettingsErrors.UserNotFound);
        }

        return await GroqModelLookup.ListForUserAsync(user, _secretProtector, _aiServiceClient, cancellationToken);
    }
}
