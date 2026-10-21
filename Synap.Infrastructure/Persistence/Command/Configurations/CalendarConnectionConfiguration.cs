using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Command.Configurations;

/// <summary>calendar-integration design.md Decisión 2.</summary>
public sealed class CalendarConnectionConfiguration : IEntityTypeConfiguration<CalendarConnection>
{
    public void Configure(EntityTypeBuilder<CalendarConnection> builder)
    {
        builder.ToTable("calendar_connections");

        // La clave es el usuario: como mucho una conexión cada uno (specs/calendar-integration).
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id)
            .HasColumnName("user_id")
            .ValueGeneratedNever()
            .HasConversion(id => id.Value, value => UserId.CreateFromDatabase(value));

        builder.Property(c => c.Provider).HasColumnName("provider").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.AccountLabel).HasColumnName("account_label").HasMaxLength(320).IsRequired();
        builder.Property(c => c.CalendarRef).HasColumnName("calendar_ref").HasMaxLength(2048).IsRequired();
        builder.Property(c => c.CredentialsEncrypted).HasColumnName("credentials_encrypted").IsRequired();
        builder.Property(c => c.ConnectedAt).HasColumnName("connected_at").IsRequired();
        builder.Property(c => c.NeedsReconnectSince).HasColumnName("needs_reconnect_since");
        builder.Ignore(c => c.NeedsReconnect);

        builder.Property(c => c.FechaCreacion)
            .HasColumnName("created_at")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(c => c.FechaCreacion).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        // Se va con la fila del usuario, como los recordatorios (specs/calendar-integration "Cuenta borrada").
        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<CalendarConnection>(c => c.Id)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
