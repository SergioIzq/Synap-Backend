using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Command.Configurations;

/// <summary>assistant-reminders design.md Decision 7.</summary>
public sealed class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
    /// <summary>Stored as the same short string the LLM and the web app's selector produce.</summary>
    private static readonly ValueConverter<Recurrence, string> RecurrenceConverter =
        new(recurrence => recurrence.ToString(), stored => Recurrence.CreateFromDatabase(stored));

    public void Configure(EntityTypeBuilder<Reminder> builder)
    {
        builder.ToTable("reminders");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .HasColumnName("id")
            .ValueGeneratedNever()
            .HasConversion(id => id.Value, value => ReminderId.CreateFromDatabase(value));

        builder.Property(r => r.UserId)
            .HasColumnName("user_id")
            .IsRequired()
            .HasConversion(userId => userId.Value, value => UserId.CreateFromDatabase(value));

        builder.Property(r => r.Text)
            .HasColumnName("text")
            .HasMaxLength(Reminder.MaxTextLength)
            .IsRequired();

        builder.Property(r => r.NoteId)
            .HasColumnName("note_id")
            .HasConversion(noteId => noteId!.Value.Value, value => NoteId.CreateFromDatabase(value));

        builder.Property(r => r.DueAt).HasColumnName("due_at").IsRequired();
        builder.Property(r => r.SentAt).HasColumnName("sent_at");
        builder.Property(r => r.DismissedAt).HasColumnName("dismissed_at");

        builder.Property(r => r.Recurrence)
            .HasColumnName("recurrence")
            .HasMaxLength(Recurrence.MaxLength)
            .HasConversion(RecurrenceConverter);

        builder.Property(r => r.FechaCreacion)
            .HasColumnName("created_at")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(r => r.FechaCreacion).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        // Like memory entries, reminders go with the user row itself (specs/identity "Delete
        // account"): no explicit DELETE needed in UserWriteRepository.DeleteWithAllDataAsync.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // A deleted note leaves its reminders standing, without the link (specs/reminders).
        builder.HasOne<Note>()
            .WithMany()
            .HasForeignKey(r => r.NoteId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(r => r.UserId).HasDatabaseName("idx_reminders_user_id");

        // What the delivery poller sweeps every minute (design.md Decision 1): the filter keeps the
        // index to the handful of rows still waiting rather than every reminder ever created.
        builder.HasIndex(r => r.DueAt)
            .HasDatabaseName("idx_reminders_due_pending")
            .HasFilter("sent_at IS NULL");
    }
}
