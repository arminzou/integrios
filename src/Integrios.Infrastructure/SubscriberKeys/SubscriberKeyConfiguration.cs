using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.SubscriberKeys;

internal sealed class SubscriberKeyConfiguration : IEntityTypeConfiguration<SubscriberKey>
{
    public void Configure(EntityTypeBuilder<SubscriberKey> entity)
    {
        entity.HasKey(e => e.Id).HasName("subscriber_keys_pkey");

        entity.ToTable("subscriber_keys");

        entity.HasAlternateKey(e => e.KeyPrefix).HasName("subscriber_keys_key_prefix_key");

        entity.HasIndex(e => e.KeyHash, "idx_subscriber_keys_key_hash").IsUnique();

        entity.HasIndex(e => new { e.TenantId, e.DestinationId }, "idx_subscriber_keys_destination");

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Description).HasColumnName("description");
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        entity.Property(e => e.KeyHash).HasColumnName("key_hash");
        entity.Property(e => e.KeyPrefix).HasColumnName("key_prefix");
        entity.Property(e => e.LastUsedAt).HasColumnName("last_used_at");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
        entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        entity.Property(e => e.DestinationId).HasColumnName("destination_id");

        entity.HasOne<Destination>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.DestinationId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("subscriber_keys_destination_fkey");

        entity.HasQueryFilter(subscriberKey => subscriberKey.DeletedAt == null);
    }
}
