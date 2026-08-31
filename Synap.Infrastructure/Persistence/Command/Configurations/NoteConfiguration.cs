using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Command.Configurations;

public sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.ToTable("notes");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Id)
            .HasColumnName("id")
            .ValueGeneratedNever()
            .HasConversion(id => id.Value, value => NoteId.CreateFromDatabase(value));

        builder.Property(n => n.UserId)
            .HasColumnName("user_id")
            .IsRequired()
            .HasConversion(userId => userId.Value, value => UserId.CreateFromDatabase(value));

        builder.Property(n => n.Type)
            .HasColumnName("note_type")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(n => n.Title).HasColumnName("title").HasMaxLength(200);

        // No column type conversion for Content: unlike Kash's varchar-length Value Objects,
        // notes/code snippets are unbounded free text - "text" stores the content exactly as
        // submitted (specs/knowledge-vault "preserving whitespace and indentation").
        builder.Property(n => n.Content).HasColumnName("content").HasColumnType("text").IsRequired();

        builder.Property(n => n.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.Property(n => n.FechaCreacion)
            .HasColumnName("created_at")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(n => n.FechaCreacion).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        builder.OwnsOne(n => n.Metadata, metadata =>
        {
            metadata.ToTable("notes"); // same table - owned type columns, not a separate one.
            metadata.Property(m => m.ExtractedTitle).HasColumnName("metadata_title").HasMaxLength(500);
            metadata.Property(m => m.ExtractedDescription).HasColumnName("metadata_description").HasMaxLength(1000);
            metadata.Property(m => m.ImageUrl).HasColumnName("metadata_image_url").HasMaxLength(2000);
        });

        builder.HasIndex(n => new { n.UserId, n.FechaCreacion }).HasDatabaseName("idx_notes_user_created");

        builder.HasMany(n => n.Tags)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "note_tags",
                join => join.HasOne<Tag>().WithMany().HasForeignKey("tag_id").OnDelete(DeleteBehavior.Cascade),
                join => join.HasOne<Note>().WithMany().HasForeignKey("note_id").OnDelete(DeleteBehavior.Cascade),
                join =>
                {
                    join.HasKey("note_id", "tag_id");
                    join.ToTable("note_tags");
                });
    }
}
