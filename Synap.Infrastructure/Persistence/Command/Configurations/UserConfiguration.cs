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

        builder.Property(u => u.FechaCreacion)
            .HasColumnName("created_at")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(u => u.FechaCreacion)
            .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
