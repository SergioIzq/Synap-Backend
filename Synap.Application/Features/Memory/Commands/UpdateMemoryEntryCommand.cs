using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Memory.Commands;

public sealed record UpdateMemoryEntryCommand(Guid EntryId, string? Text) : ICommand<MemoryEntrySummary>;

public sealed class UpdateMemoryEntryCommandHandler : ICommandHandler<UpdateMemoryEntryCommand, MemoryEntrySummary>
{
    private readonly IMemoryEntryWriteRepository _memoryEntryWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public UpdateMemoryEntryCommandHandler(IMemoryEntryWriteRepository memoryEntryWriteRepository, IUnitOfWork unitOfWork, IUserContext userContext)
    {
        _memoryEntryWriteRepository = memoryEntryWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result<MemoryEntrySummary>> Handle(UpdateMemoryEntryCommand request, CancellationToken cancellationToken)
    {
        // Another user's entry looks exactly like a missing one (specs/assistant-memory).
        var entry = await _memoryEntryWriteRepository.GetOwnedByUserAsync(request.EntryId, _userContext.RequireUserId(), cancellationToken);
        if (entry is null)
        {
            return Result.Failure<MemoryEntrySummary>(MemoryEntry.NotFound);
        }

        var edited = entry.Edit(request.Text);
        if (edited.IsFailure)
        {
            return Result.Failure<MemoryEntrySummary>(edited.Error);
        }

        _memoryEntryWriteRepository.Update(entry);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(MemoryEntrySummary.From(entry));
    }
}
