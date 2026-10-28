using Microsoft.Extensions.Options;
using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Settings.Queries;

public sealed record GetSettingsQuery : IQuery<SettingsResponse>;

public sealed class GetSettingsQueryHandler : IQueryHandler<GetSettingsQuery, SettingsResponse>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUserContext _userContext;
    private readonly ICalendarConnectionReadRepository _calendarConnections;
    private readonly AiOptions _aiOptions;

    public GetSettingsQueryHandler(
        IUserWriteRepository userWriteRepository,
        IUserContext userContext,
        ICalendarConnectionReadRepository calendarConnections,
        IOptions<AiOptions> aiOptions)
    {
        _userWriteRepository = userWriteRepository;
        _userContext = userContext;
        _calendarConnections = calendarConnections;
        _aiOptions = aiOptions.Value;
    }

    public async Task<Result<SettingsResponse>> Handle(GetSettingsQuery request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure<SettingsResponse>(SettingsErrors.UserNotFound);
        }

        var calendar = await _calendarConnections.GetSummaryAsync(user.Id.Value, cancellationToken);

        return Result.Success(new SettingsResponse(
            user.Email.Value,
            AiSettingsResponse.From(user, _aiOptions.DefaultGroqModel),
            BriefingSettingsResponse.From(user),
            CalendarSettingsResponse.From(calendar)));
    }
}
