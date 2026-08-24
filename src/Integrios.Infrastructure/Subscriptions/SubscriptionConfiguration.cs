using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Subscriptions;

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> entity)
    {
        entity.HasKey(e => e.Id).HasName("routes_pkey");

        entity.ToTable("subscriptions");

        entity.HasIndex(e => e.TopicId, "idx_subscriptions_topic_id");

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Description).HasColumnName("description");
        entity.Property(e => e.DestinationConnectionId).HasColumnName("destination_connection_id");
        entity.Property(e => e.HttpDelivery)
            .HasDefaultValueSql("'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'::jsonb")
            .HasColumnType("jsonb")
            .HasColumnName("http_delivery");
        entity.Property(e => e.MatchRules)
            .HasDefaultValueSql("'{}'::jsonb")
            .HasColumnType("jsonb")
            .HasColumnName("match_rules");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.OrderIndex)
            .HasDefaultValue(0)
            .HasColumnName("order_index");
        entity.Property(e => e.Status)
            .HasDefaultValueSql("'active'::text")
            .HasColumnName("status");
        entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        entity.Property(e => e.TopicId).HasColumnName("topic_id");
        entity.Property(e => e.MappingConfig)
            .HasColumnType("jsonb")
            .HasColumnName("mapping_config");
        entity.Property(e => e.UpdatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("updated_at");

        entity.HasOne<Connection>().WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(d => new { d.TenantId, d.DestinationConnectionId })
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("fk_subscriptions_destination_connection_tenant");

        entity.HasOne<Topic>().WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(d => new { d.TenantId, d.TopicId })
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("fk_subscriptions_topic_tenant");
    }
}
