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

    public Task<TEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Context.Set<TEntity>().FirstOrDefaultAsync(entity => entity.Id.Value == id, cancellationToken);

    public void Add(TEntity entity) => Context.Set<TEntity>().Add(entity);

    public async Task CreateAsync(TEntity entity, CancellationToken cancellationToken)
        => await Context.Set<TEntity>().AddAsync(entity, cancellationToken);

    public void Update(TEntity entity) => Context.Set<TEntity>().Update(entity);

    public void Delete(TEntity entity) => Context.Set<TEntity>().Remove(entity);
}
