using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Application.Features.Memory.Commands;

/// <summary>specs/assistant-memory - used by the Settings page and by the assistant's `remember` action alike.</summary>
public sealed record AddMemoryEntryCommand(string? Text) : ICommand<MemoryEntrySummary>;

public sealed class AddMemoryEntryCommandHandler : ICommandHandler<AddMemoryEntryCommand, MemoryEntrySummary>
{
    private readonly IMemoryEntryWriteRepository _memoryEntryWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public AddMemoryEntryCommandHandler(IMemoryEntryWriteRepository memoryEntryWriteRepository, IUnitOfWork unitOfWork, IUserContext userContext)
    {
        _memoryEntryWriteRepository = memoryEntryWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result<MemoryEntrySummary>> Handle(AddMemoryEntryCommand request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        var entry = MemoryEntry.Create(UserId.CreateFromDatabase(userId), request.Text);
        if (entry.IsFailure)
        {
            return Result.Failure<MemoryEntrySummary>(entry.Error);
        }

        // specs/assistant-memory "Memory full": the stored memory stays unchanged.
        if (await _memoryEntryWriteRepository.CountByUserAsync(userId, cancellationToken) >= MemoryEntry.MaxEntriesPerUser)
        {
            return Result.Failure<MemoryEntrySummary>(MemoryEntry.MemoryFull);
        }

        await _memoryEntryWriteRepository.CreateAsync(entry.Value, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(MemoryEntrySummary.From(entry.Value));
    }
}
