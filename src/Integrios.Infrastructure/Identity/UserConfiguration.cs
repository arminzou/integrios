using Integrios.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Identity;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> entity)
    {
        entity.HasKey(e => e.Id).HasName("users_pkey");

        entity.ToTable("users");

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.DisplayName).HasColumnName("display_name");
        // Descriptive only. Deliberately not unique: email equality must never link two identities.
        entity.Property(e => e.Email).HasColumnName("email");
        entity.Property(e => e.LastSignedInAt).HasColumnName("last_signed_in_at");
    }
}
