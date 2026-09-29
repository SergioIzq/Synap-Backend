using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Command.Configurations;

public sealed class MemoryEntryConfiguration : IEntityTypeConfiguration<MemoryEntry>
{
    public void Configure(EntityTypeBuilder<MemoryEntry> builder)
    {
        builder.ToTable("memory_entries");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id)
            .HasColumnName("id")
            .ValueGeneratedNever()
            .HasConversion(id => id.Value, value => MemoryEntryId.CreateFromDatabase(value));

        builder.Property(m => m.UserId)
            .HasColumnName("user_id")
            .IsRequired()
            .HasConversion(userId => userId.Value, value => UserId.CreateFromDatabase(value));

        builder.Property(m => m.Text)
            .HasColumnName("text")
            .HasMaxLength(MemoryEntry.MaxTextLength)
            .IsRequired();

        builder.Property(m => m.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.Property(m => m.FechaCreacion)
            .HasColumnName("created_at")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(m => m.FechaCreacion).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        // Unlike notes and tags, memory goes with the user row itself (specs/identity "Delete
        // account"): no explicit DELETE needed in UserWriteRepository.DeleteWithAllDataAsync.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => m.UserId).HasDatabaseName("idx_memory_entries_user_id");
    }
}
