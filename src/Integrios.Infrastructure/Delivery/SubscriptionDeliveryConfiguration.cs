using Integrios.Domain.Connections;
using Integrios.Domain.Delivery;
using Integrios.Domain.Events;
using Integrios.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainEvent = Integrios.Domain.Events.Event;

namespace Integrios.Infrastructure.Delivery;

internal sealed class SubscriptionDeliveryConfiguration : IEntityTypeConfiguration<SubscriptionDelivery>
{
    public void Configure(EntityTypeBuilder<SubscriptionDelivery> entity)
    {
        entity.HasKey(e => e.Id).HasName("subscription_deliveries_pkey");

        entity.ToTable("subscription_deliveries", table =>
        {
            table.HasCheckConstraint(
                "ck_subscription_deliveries_attempt_counts_nonnegative",
                "lifetime_attempt_count >= 0 "
                + "AND retry_cycle_attempt_count >= 0 "
                + "AND retry_cycle_attempt_count <= lifetime_attempt_count");
            table.HasCheckConstraint(
                "ck_subscription_deliveries_lease_state",
                "((status = 'in_flight' AND active_attempt_id IS NOT NULL AND lease_expires_at IS NOT NULL) "
                + "OR (status IN ('pending', 'succeeded', 'dead_lettered') "
                + "AND active_attempt_id IS NULL AND lease_expires_at IS NULL))");
        });

        entity.HasIndex(e => new { e.Status, e.LeaseExpiresAt, e.DeliverAfter, e.CreatedAt }, "idx_subscription_deliveries_claimable").HasFilter("(status = ANY (ARRAY['pending'::text, 'in_flight'::text]))");

        entity.HasIndex(e => e.EventId, "idx_subscription_deliveries_event_id");

        entity.HasIndex(e => e.SubscriptionId, "idx_subscription_deliveries_subscription_id");

        entity.HasAlternateKey(e => new { e.EventId, e.SubscriptionId })
            .HasName("uq_subscription_deliveries_event_subscription");

        entity.Property(e => e.Id)
            .HasDefaultValueSql("gen_random_uuid()")
            .HasColumnName("id");
        entity.Property(e => e.ActiveAttemptId).HasColumnName("active_attempt_id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.DeliverAfter).HasColumnName("deliver_after");
        entity.Property(e => e.DestinationConnectionId).HasColumnName("destination_connection_id");
        entity.Property(e => e.EventId).HasColumnName("event_id");
        entity.Property(e => e.FailedAt).HasColumnName("failed_at");
        entity.Property(e => e.HttpExecutionSnapshot)
            .HasColumnType("jsonb")
            .HasColumnName("http_execution_snapshot");
        entity.Property(e => e.IntegrationKey).HasColumnName("integration_key");
        entity.Property(e => e.LeaseExpiresAt).HasColumnName("lease_expires_at");
        entity.Property(e => e.LifetimeAttemptCount)
            .HasDefaultValue(0)
            .HasColumnName("lifetime_attempt_count");
        entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
        entity.Property(e => e.RetryCycleAttemptCount)
            .HasDefaultValue(0)
            .HasColumnName("retry_cycle_attempt_count");
        entity.Property(e => e.Status)
            .HasDefaultValueSql("'pending'::text")
            .HasColumnName("status");
        entity.Property(e => e.SubscriptionId).HasColumnName("subscription_id");
        entity.Property(e => e.Traceparent).HasColumnName("traceparent");
        entity.Property(e => e.TransformConfigSnapshot)
            .HasColumnType("jsonb")
            .HasColumnName("transform_config_snapshot");
        entity.Property(e => e.UpdatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("updated_at");

        entity.HasOne<Connection>().WithMany()
            .HasForeignKey(d => d.DestinationConnectionId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("subscription_deliveries_destination_connection_id_fkey");

        entity.HasOne<DomainEvent>().WithMany()
            .HasForeignKey(d => d.EventId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("subscription_deliveries_event_id_fkey");

        entity.HasOne<Subscription>().WithMany()
            .HasForeignKey(d => d.SubscriptionId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("subscription_deliveries_subscription_id_fkey");

        entity.HasOne<DeliveryAttempt>().WithMany()
            .HasPrincipalKey(p => new { p.SubscriptionDeliveryId, p.Id })
            .HasForeignKey(d => new { d.Id, d.ActiveAttemptId })
            .HasConstraintName("fk_subscription_deliveries_active_attempt");
    }
}
