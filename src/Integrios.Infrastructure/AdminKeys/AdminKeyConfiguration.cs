using Integrios.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.AdminKeys;

internal sealed class AdminKeyConfiguration : IEntityTypeConfiguration<AdminKey>
{
    public void Configure(EntityTypeBuilder<AdminKey> entity)
    {
        entity.HasKey(e => e.Id).HasName("admin_keys_pkey");

        entity.ToTable("admin_keys");

        entity.HasIndex(e => e.PublicKey, "admin_keys_public_key_key").IsUnique();

        entity.HasIndex(e => e.PublicKey, "idx_admin_keys_lookup").HasFilter("(revoked_at IS NULL)");

        entity.Property(e => e.Id)
            .HasDefaultValueSql("gen_random_uuid()")
            .HasColumnName("id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.PublicKey).HasColumnName("public_key");
        entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
        entity.Property(e => e.SecretHash).HasColumnName("secret_hash");
    }
}
