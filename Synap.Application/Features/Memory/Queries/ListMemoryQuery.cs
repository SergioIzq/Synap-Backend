using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Memory.Queries;

/// <summary>The "Memoria" section: entries plus the limits the page shows ("n/25", max length).</summary>
public sealed record MemoryResponse(IReadOnlyList<MemoryEntrySummary> Entries, int MaxEntries, int MaxTextLength);

public sealed record ListMemoryQuery : IQuery<MemoryResponse>;

public sealed class ListMemoryQueryHandler : IQueryHandler<ListMemoryQuery, MemoryResponse>
{
    private readonly IMemoryEntryReadRepository _memoryEntryReadRepository;
    private readonly IUserContext _userContext;

    public ListMemoryQueryHandler(IMemoryEntryReadRepository memoryEntryReadRepository, IUserContext userContext)
    {
        _memoryEntryReadRepository = memoryEntryReadRepository;
        _userContext = userContext;
    }

    public async Task<Result<MemoryResponse>> Handle(ListMemoryQuery request, CancellationToken cancellationToken)
    {
        var entries = await _memoryEntryReadRepository.ListByUserAsync(_userContext.RequireUserId(), cancellationToken);
        return Result.Success(new MemoryResponse(entries, MemoryEntry.MaxEntriesPerUser, MemoryEntry.MaxTextLength));
    }
}
