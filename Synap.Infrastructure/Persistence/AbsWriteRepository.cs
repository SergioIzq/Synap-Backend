using Microsoft.EntityFrameworkCore;
using SergioIzq.Domain.Kernel.Abstractions;
using SergioIzq.Domain.Kernel.Interfaces;
using SergioIzq.Domain.Kernel.Interfaces.Repositories;
using Synap.Infrastructure.Persistence.Command;

namespace Synap.Infrastructure.Persistence;

/// <summary>
/// Generic EF Core write repository, standing in for the kernel's AbsWriteRepository
/// (SergioIzq.Infrastructure.Kernel is MySQL-only - see design.md Decision 7).
/// </summary>
public abstract class AbsWriteRepository<TEntity, TId> : IWriteRepository<TEntity, TId>
    where TEntity : AbsEntity<TId>
    where TId : struct, IGuidValueObject
{
    protected readonly SynapDbContext Context;

    protected AbsWriteRepository(SynapDbContext context)
    {
        Context = context;
    }

    // Explicitly tracked regardless of the DbContext's configured default tracking behavior
    // (Production defaults to NoTracking for plain reads - see Infrastructure/DependencyInjection.cs):
    // callers fetch through the write side specifically to mutate and save, and relying on
    // disconnected-graph Update() semantics for entities with collection navigations (Note.Tags)
    // is a well-known EF Core footgun.
    //
    // FindAsync with the strongly-typed key rather than `entity.Id.Value == id`: EF can't
    // translate `.Value` on a value-converted key ("could not be translated" at runtime), while
    // FindAsync goes through the key's converter and always returns a tracked entity. Every ID
    // value object (UserId, NoteId, TagId) has a public (Guid) constructor.
    public async Task<TEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var key = (TId)Activator.CreateInstance(typeof(TId), id)!;
        return await Context.Set<TEntity>().FindAsync([key], cancellationToken);
    }

    public void Add(TEntity entity) => Context.Set<TEntity>().Add(entity);

    public async Task CreateAsync(TEntity entity, CancellationToken cancellationToken)
        => await Context.Set<TEntity>().AddAsync(entity, cancellationToken);

    public void Update(TEntity entity) => Context.Set<TEntity>().Update(entity);

    public void Delete(TEntity entity) => Context.Set<TEntity>().Remove(entity);
}
