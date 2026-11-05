using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Command.Configurations;

/// <summary>calendar-store-and-view design.md Decisión 2.</summary>
public sealed class CalendarConfiguration : IEntityTypeConfiguration<Domain.Calendar>
{
    public void Configure(EntityTypeBuilder<Domain.Calendar> builder)
    {
        builder.ToTable("calendars");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("id")
            .ValueGeneratedNever()
            .HasConversion(id => id.Value, value => CalendarId.CreateFromDatabase(value));

        builder.Property(c => c.UserId)
            .HasColumnName("user_id")
            .IsRequired()
            .HasConversion(userId => userId.Value, value => UserId.CreateFromDatabase(value));

        builder.Property(c => c.Href).HasColumnName("href").HasMaxLength(Domain.Calendar.MaxHrefLength).IsRequired();
        builder.Property(c => c.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(Domain.Calendar.MaxDisplayNameLength)
            .IsRequired();
        builder.Property(c => c.ServerColor).HasColumnName("server_color").HasMaxLength(7);
        builder.Property(c => c.Color).HasColumnName("color").HasMaxLength(7);

        // Como texto, para que la columna se lea igual que la API (design.md Decisión 2).
        builder.Property(c => c.Scope)
            .HasColumnName("scope")
            .HasMaxLength(16)
            .IsRequired()
            .HasConversion(scope => CalendarScopes.ToText(scope), text => CalendarScopes.Parse(text) ?? CalendarScope.Personal);

        builder.Property(c => c.Enabled).HasColumnName("enabled").IsRequired();
        builder.Property(c => c.SyncToken).HasColumnName("sync_token").HasMaxLength(2048);
        builder.Property(c => c.CTag).HasColumnName("ctag").HasMaxLength(512);
        builder.Property(c => c.LastSyncedAt).HasColumnName("last_synced_at");
        builder.Property(c => c.LastSyncError).HasColumnName("last_sync_error").HasMaxLength(64);

        builder.Ignore(c => c.EffectiveColor);

        builder.Property(c => c.FechaCreacion)
            .HasColumnName("created_at")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(c => c.FechaCreacion).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        // Los calendarios cuelgan de la conexión: desconectar o borrar la cuenta (que borra la
        // conexión) se los lleva con sus eventos (specs/calendar-integration "La copia se borra con
        // la conexión").
        builder.HasOne<CalendarConnection>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .HasPrincipalKey(c => c.Id)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.UserId, c.Href })
            .IsUnique()
            .HasDatabaseName("ux_calendars_user_href");
    }
}

/// <summary>calendar-store-and-view design.md Decisión 1.</summary>
public sealed class CalendarEventResourceConfiguration : IEntityTypeConfiguration<CalendarEventResource>
{
    public void Configure(EntityTypeBuilder<CalendarEventResource> builder)
    {
        builder.ToTable("calendar_events");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .ValueGeneratedNever()
            .HasConversion(id => id.Value, value => CalendarEventId.CreateFromDatabase(value));

        builder.Property(e => e.CalendarId)
            .HasColumnName("calendar_id")
            .IsRequired()
            .HasConversion(id => id.Value, value => CalendarId.CreateFromDatabase(value));

        builder.Property(e => e.UserId)
            .HasColumnName("user_id")
            .IsRequired()
            .HasConversion(userId => userId.Value, value => UserId.CreateFromDatabase(value));

        builder.Property(e => e.Href).HasColumnName("href").HasMaxLength(CalendarEventResource.MaxHrefLength).IsRequired();
        builder.Property(e => e.ETag).HasColumnName("etag").HasMaxLength(CalendarEventResource.MaxETagLength);
        builder.Property(e => e.Uid).HasColumnName("uid").HasMaxLength(CalendarEventResource.MaxUidLength).IsRequired();
        builder.Property(e => e.Ics).HasColumnName("ics").IsRequired();
        builder.Property(e => e.Summary).HasColumnName("summary").HasMaxLength(CalendarEventResource.MaxSummaryLength);
        builder.Property(e => e.StartsAtUtc).HasColumnName("starts_at_utc").IsRequired();
        builder.Property(e => e.EndsAtUtc).HasColumnName("ends_at_utc");
        builder.Property(e => e.IsRecurring).HasColumnName("is_recurring").IsRequired();
        builder.Property(e => e.AllDay).HasColumnName("all_day").IsRequired();

        builder.Property(e => e.FechaCreacion)
            .HasColumnName("created_at")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(e => e.FechaCreacion).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        builder.HasOne<Domain.Calendar>()
            .WithMany()
            .HasForeignKey(e => e.CalendarId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.UserId, e.StartsAtUtc }).HasDatabaseName("idx_calendar_events_user_starts");
        builder.HasIndex(e => new { e.CalendarId, e.Href })
            .IsUnique()
            .HasDatabaseName("ux_calendar_events_calendar_href");
    }
}
