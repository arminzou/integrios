using Integrios.Domain.Common;
using Integrios.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.ApiKeys;

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> entity)
    {
        entity.HasKey(e => e.Id).HasName("api_credentials_pkey");

        entity.ToTable("api_keys");

        entity.HasAlternateKey(e => e.KeyPrefix).HasName("api_credentials_key_id_key");

        entity.HasIndex(e => e.KeyHash, "idx_api_keys_key_hash").IsUnique();

        entity.HasIndex(e => e.TenantId, "idx_api_keys_tenant_id");

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Description).HasColumnName("description");
        entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        entity.Property(e => e.KeyHash).HasColumnName("key_hash");
        entity.Property(e => e.KeyPrefix).HasColumnName("key_prefix");
        entity.Property(e => e.LastUsedAt).HasColumnName("last_used_at");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
        entity.Property(e => e.Status)
            .HasDefaultValueSql("'active'::text")
            .HasColumnName("status");
        entity.Property(e => e.TenantId).HasColumnName("tenant_id");

        entity.HasOne<Tenant>().WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("api_credentials_tenant_id_fkey");
    }
}
