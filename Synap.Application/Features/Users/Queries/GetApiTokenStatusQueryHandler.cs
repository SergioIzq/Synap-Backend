using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;

namespace Synap.Application.Features.Users.Queries;

public sealed class GetApiTokenStatusQueryHandler : IQueryHandler<GetApiTokenStatusQuery, ApiTokenStatusResponse>
{
    private readonly IUserWriteRepository _userWriteRepository;

    public GetApiTokenStatusQueryHandler(IUserWriteRepository userWriteRepository)
    {
        _userWriteRepository = userWriteRepository;
    }

    public async Task<Result<ApiTokenStatusResponse>> Handle(GetApiTokenStatusQuery request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<ApiTokenStatusResponse>(Error.NotFound("User not found."));
        }

        return Result.Success(new ApiTokenStatusResponse(user.ApiTokenHash is not null, user.ApiTokenCreatedAt));
    }
}
