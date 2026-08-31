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

    public AskAssistantQueryHandler(IAiServiceClient aiServiceClient, IUserContext userContext)
    {
        _aiServiceClient = aiServiceClient;
        _userContext = userContext;
    }

    public async Task<Result<AssistantAnswer>> Handle(AskAssistantQuery request, CancellationToken cancellationToken)
    {
        var answer = await _aiServiceClient.AskAsync(_userContext.RequireUserId(), request.Question, cancellationToken);
        return Result.Success(answer);
    }
}
