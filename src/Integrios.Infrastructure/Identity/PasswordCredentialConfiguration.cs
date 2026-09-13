using Integrios.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Identity;

internal sealed class PasswordCredentialConfiguration : IEntityTypeConfiguration<PasswordCredential>
{
    public void Configure(EntityTypeBuilder<PasswordCredential> entity)
    {
        entity.HasKey(e => e.Id).HasName("password_credentials_pkey");

        entity.ToTable("password_credentials", table => table.HasCheckConstraint(
            "ck_password_credentials_session_revision_positive",
            "session_revision > 0"));

        entity.HasIndex(e => e.UserId, "uq_password_credentials_user_id").IsUnique();
        entity.HasIndex(e => e.NormalizedEmail, "uq_password_credentials_normalized_email").IsUnique();

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.UserId).HasColumnName("user_id");
        entity.Property(e => e.Email)
            .HasMaxLength(320)
            .HasColumnName("email");
        entity.Property(e => e.NormalizedEmail)
            .HasMaxLength(320)
            .HasColumnName("normalized_email");
        entity.Property(e => e.PasswordHash)
            .HasMaxLength(1024)
            .HasColumnName("password_hash");
        entity.Property(e => e.SessionRevision)
            .HasDefaultValue(1)
            .HasColumnName("session_revision");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("updated_at");
        entity.Property(e => e.DisabledAt).HasColumnName("disabled_at");

        entity.HasOne<User>().WithOne()
            .HasForeignKey<PasswordCredential>(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("password_credentials_user_id_fkey");
    }
}
