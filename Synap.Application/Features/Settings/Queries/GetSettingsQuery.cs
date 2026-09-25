using Microsoft.Extensions.Options;
using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Settings.Queries;

public sealed record GetSettingsQuery : IQuery<SettingsResponse>;

public sealed class GetSettingsQueryHandler : IQueryHandler<GetSettingsQuery, SettingsResponse>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUserContext _userContext;
    private readonly AiOptions _aiOptions;

    public GetSettingsQueryHandler(IUserWriteRepository userWriteRepository, IUserContext userContext, IOptions<AiOptions> aiOptions)
    {
        _userWriteRepository = userWriteRepository;
        _userContext = userContext;
        _aiOptions = aiOptions.Value;
    }

    public async Task<Result<SettingsResponse>> Handle(GetSettingsQuery request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure<SettingsResponse>(SettingsErrors.UserNotFound);
        }

        return Result.Success(new SettingsResponse(user.Email.Value, AiSettingsResponse.From(user, _aiOptions.DefaultGroqModel)));
    }
}
