using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Memory.Commands;

public sealed record DeleteMemoryEntryCommand(Guid EntryId) : ICommand;

public sealed class DeleteMemoryEntryCommandHandler : ICommandHandler<DeleteMemoryEntryCommand>
{
    private readonly IMemoryEntryWriteRepository _memoryEntryWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public DeleteMemoryEntryCommandHandler(IMemoryEntryWriteRepository memoryEntryWriteRepository, IUnitOfWork unitOfWork, IUserContext userContext)
    {
        _memoryEntryWriteRepository = memoryEntryWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result> Handle(DeleteMemoryEntryCommand request, CancellationToken cancellationToken)
    {
        var entry = await _memoryEntryWriteRepository.GetOwnedByUserAsync(request.EntryId, _userContext.RequireUserId(), cancellationToken);
        if (entry is null)
        {
            return Result.Failure(MemoryEntry.NotFound);
        }

        _memoryEntryWriteRepository.Delete(entry);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record DeleteAllMemoryCommand : ICommand;

public sealed class DeleteAllMemoryCommandHandler : ICommandHandler<DeleteAllMemoryCommand>
{
    private readonly IMemoryEntryWriteRepository _memoryEntryWriteRepository;
    private readonly IUserContext _userContext;

    public DeleteAllMemoryCommandHandler(IMemoryEntryWriteRepository memoryEntryWriteRepository, IUserContext userContext)
    {
        _memoryEntryWriteRepository = memoryEntryWriteRepository;
        _userContext = userContext;
    }

    public async Task<Result> Handle(DeleteAllMemoryCommand request, CancellationToken cancellationToken)
    {
        // A single set-based DELETE - nothing to track or save.
        await _memoryEntryWriteRepository.DeleteAllByUserAsync(_userContext.RequireUserId(), cancellationToken);
        return Result.Success();
    }
}
