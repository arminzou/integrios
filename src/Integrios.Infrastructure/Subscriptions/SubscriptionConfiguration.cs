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

        entity.ToTable("subscriptions", table =>
            table.HasCheckConstraint("ck_subscriptions_event_types_array", "jsonb_typeof(event_types) = 'array'"));

        entity.HasIndex(e => e.TopicId, "idx_subscriptions_topic_id");

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Description).HasColumnName("description");
        entity.Property(e => e.DestinationId).HasColumnName("destination_id");
        entity.Property(e => e.HttpDelivery)
            .HasDefaultValueSql("'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'::jsonb")
            .HasColumnType("jsonb")
            .HasColumnName("http_delivery");
        entity.Property(e => e.HttpSuccess).HasColumnType("jsonb").HasColumnName("http_success");
        entity.Property(e => e.EventTypes)
            .HasColumnType("jsonb")
            .HasColumnName("event_types");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.OrderIndex)
            .HasDefaultValue(0)
            .HasColumnName("order_index");
        entity.Property(e => e.Status)
            .HasColumnName("status");
        entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        entity.Property(e => e.TopicId).HasColumnName("topic_id");
        entity.Property(e => e.MappingConfig)
            .HasColumnType("jsonb")
            .HasColumnName("mapping_config");
        entity.Property(e => e.UpdatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("updated_at");

        entity.HasOne<Destination>().WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(d => new { d.TenantId, d.DestinationId })
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("fk_subscriptions_destination_tenant");

        entity.HasOne<Topic>().WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(d => new { d.TenantId, d.TopicId })
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("fk_subscriptions_topic_tenant");
    }
}
