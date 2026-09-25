using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Domain.Errors;
using Synap.Shared.Application;

namespace Synap.Application.Features.Users.Queries;

/// <summary>specs/identity "Current user profile".</summary>
public sealed record GetCurrentUserQuery : IQuery<CurrentUserResponse>;

public sealed record CurrentUserResponse(string Email, DateTime CreatedAt);

public sealed class GetCurrentUserQueryHandler : IQueryHandler<GetCurrentUserQuery, CurrentUserResponse>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUserContext _userContext;

    public GetCurrentUserQueryHandler(IUserWriteRepository userWriteRepository, IUserContext userContext)
    {
        _userWriteRepository = userWriteRepository;
        _userContext = userContext;
    }

    public async Task<Result<CurrentUserResponse>> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);

        return user is null
            ? Result.Failure<CurrentUserResponse>(UserErrors.NotFound)
            : Result.Success(new CurrentUserResponse(user.Email.Value, user.FechaCreacion));
    }
}
