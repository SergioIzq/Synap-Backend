using MediatR;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Infrastructure.Persistence.Command;

namespace Synap.Infrastructure.Persistence;

/// <summary>
/// Not the kernel's AddKernelUnitOfWork (MySQL-only, see design.md Decision 7). Domain events
/// are dispatched here, after a successful save, rather than via an EF SaveChanges interceptor -
/// equivalent as long as every write goes through this UnitOfWork (which is the only way
/// Application handlers persist changes).
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly SynapDbContext _context;
    private readonly IPublisher _publisher;

    public UnitOfWork(SynapDbContext context, IPublisher publisher)
    {
        _context = context;
        _publisher = publisher;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        var entitiesWithEvents = _context.ChangeTracker
            .Entries<IHasDomainEvents>()
            .Select(entry => entry.Entity)
            .Where(entity => entity.DomainEvents.Count > 0)
            .ToList();

        var result = await _context.SaveChangesAsync(cancellationToken);

        foreach (var entity in entitiesWithEvents)
        {
            var domainEvents = entity.DomainEvents.ToList();
            entity.ClearDomainEvents();

            foreach (var domainEvent in domainEvents)
            {
                await _publisher.Publish(domainEvent, cancellationToken);
            }
        }

        return result;
    }

    public async Task CommitTransactionAsync(CancellationToken cancellationToken)
    {
        if (_context.Database.CurrentTransaction is not null)
        {
            await _context.Database.CurrentTransaction.CommitAsync(cancellationToken);
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken cancellationToken)
    {
        if (_context.Database.CurrentTransaction is not null)
        {
            await _context.Database.CurrentTransaction.RollbackAsync(cancellationToken);
        }
    }

    // The DbContext's lifetime is owned by the DI container (scoped), not by this class -
    // nothing to dispose here, but IUnitOfWork requires the interface to be satisfied.
    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
