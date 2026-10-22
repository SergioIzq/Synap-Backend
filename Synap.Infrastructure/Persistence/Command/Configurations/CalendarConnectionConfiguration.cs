using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Command.Configurations;

public sealed class CalendarConnectionConfiguration : IEntityTypeConfiguration<CalendarConnection>
{
    public void Configure(EntityTypeBuilder<CalendarConnection> builder)
    {
        builder.ToTable("calendar_connections");
        builder.HasKey(c => c.Id);

        // La clave es el id del usuario: una conexión como mucho por usuario (design.md Decisión 2).
        builder.Property(c => c.Id)
            .HasColumnName("user_id")
            .ValueGeneratedNever()
            .HasConversion(id => id.Value, value => UserId.CreateFromDatabase(value));

        builder.Ignore(c => c.UserId);
        builder.Ignore(c => c.NeedsReconnect);

        builder.Property(c => c.Provider)
            .HasColumnName("provider")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(c => c.AccountLabel).HasColumnName("account_label").HasMaxLength(320).IsRequired();
        builder.Property(c => c.CalendarRef).HasColumnName("calendar_ref").HasMaxLength(2048).IsRequired();
        builder.Property(c => c.CredentialsEncrypted).HasColumnName("credentials_encrypted").IsRequired();
        builder.Property(c => c.ConnectedAt).HasColumnName("connected_at").IsRequired();
        builder.Property(c => c.NeedsReconnectSince).HasColumnName("needs_reconnect_since");

        builder.Property(c => c.FechaCreacion)
            .HasColumnName("created_at")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(c => c.FechaCreacion).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        // Borrar la cuenta borra la conexión sin tocar DeleteAccount (specs/calendar-integration
        // "Cuenta borrada").
        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<CalendarConnection>(c => c.Id)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
