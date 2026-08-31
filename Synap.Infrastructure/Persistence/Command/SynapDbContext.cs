using Microsoft.EntityFrameworkCore;

namespace Synap.Infrastructure.Persistence.Command;

/// <summary>
/// Not derived from a kernel base DbContext - SergioIzq.Infrastructure.Kernel is MySQL-only
/// (see design.md Decision 7), so entity registration and configuration scanning are done
/// directly here instead.
/// </summary>
public sealed class SynapDbContext : DbContext
{
    public SynapDbContext(DbContextOptions<SynapDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SynapDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
