using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Command.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Id)
            .HasColumnName("id")
            .ValueGeneratedNever()
            .HasConversion(id => id.Value, value => UserId.CreateFromDatabase(value));

        builder.Property(u => u.Email)
            .HasColumnName("email")
            .HasMaxLength(255)
            .IsRequired()
            .HasConversion(email => email.Value, value => Email.CreateFromDatabase(value));

        builder.HasIndex(u => u.Email).IsUnique();

        builder.Property(u => u.PasswordHash)
            .HasColumnName("password_hash")
            .IsRequired()
            .HasConversion(hash => hash.Value, value => PasswordHash.CreateFromDatabase(value));

        builder.Property(u => u.ApiTokenHash).HasColumnName("api_token_hash");
        builder.Property(u => u.ApiTokenCreatedAt).HasColumnName("api_token_created_at");

        builder.HasIndex(u => u.ApiTokenHash);

        builder.Property(u => u.GroqApiKeyEncrypted).HasColumnName("groq_api_key_encrypted");
        builder.Property(u => u.GroqApiKeyLast4).HasColumnName("groq_api_key_last4").HasMaxLength(4);
        builder.Property(u => u.GroqApiKeyUpdatedAt).HasColumnName("groq_api_key_updated_at");
        builder.Property(u => u.GroqModel).HasColumnName("groq_model").HasMaxLength(128);
        builder.Ignore(u => u.HasGroqApiKey);

        builder.Property(u => u.PasswordResetTokenHash).HasColumnName("password_reset_token_hash").HasMaxLength(64);
        builder.Property(u => u.PasswordResetExpiresAt).HasColumnName("password_reset_expires_at");
        builder.HasIndex(u => u.PasswordResetTokenHash);

        builder.Property(u => u.SecurityStamp).HasColumnName("security_stamp").HasMaxLength(32).IsRequired();

        // Reminder delivery over Telegram (assistant-reminders design.md Decision 7).
        builder.Property(u => u.TelegramChatId).HasColumnName("telegram_chat_id").HasMaxLength(20);
        builder.Property(u => u.TelegramLinkToken).HasColumnName("telegram_link_token").HasMaxLength(64);
        builder.Property(u => u.TelegramLinkTokenExpiresAt).HasColumnName("telegram_link_token_expires");
        builder.Property(u => u.Timezone).HasColumnName("timezone").HasMaxLength(64);
        builder.Ignore(u => u.HasTelegram);

        // The webhook looks users up by the code they sent to the bot, and delivery by chat id.
        builder.HasIndex(u => u.TelegramLinkToken);
        builder.HasIndex(u => u.TelegramChatId);

        // The morning briefing (daily-briefing design.md Decision 3). The resolved day is a date,
        // not a timestamp: the question is always "has this user been briefed on *their* today?".
        builder.Property(u => u.BriefingEnabled).HasColumnName("briefing_enabled").IsRequired().HasDefaultValue(false);
        builder.Property(u => u.BriefingHour).HasColumnName("briefing_hour");
        builder.Property(u => u.BriefingLastResolvedOn).HasColumnName("briefing_last_resolved_on");

        // The sweep asks for the users it might owe a briefing, which is nobody until someone
        // turns it on - a filtered index keeps that question free on a deployment where no one has.
        builder.HasIndex(u => u.BriefingEnabled).HasFilter("briefing_enabled");

        builder.Property(u => u.FechaCreacion)
            .HasColumnName("created_at")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(u => u.FechaCreacion)
            .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
