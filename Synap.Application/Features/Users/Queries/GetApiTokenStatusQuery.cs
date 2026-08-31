using SergioIzq.Application.Kernel.Messaging;

namespace Synap.Application.Features.Users.Queries;

public sealed record GetApiTokenStatusQuery(Guid UserId) : IQuery<ApiTokenStatusResponse>;

public sealed record ApiTokenStatusResponse(bool HasToken, DateTime? CreatedAt);
