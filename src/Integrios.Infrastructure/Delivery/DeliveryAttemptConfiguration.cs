using Integrios.Domain.Delivery;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Delivery;

internal sealed class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<DeliveryAttempt> entity)
    {
        entity.HasKey(e => e.Id).HasName("delivery_attempts_pkey");

        entity.ToTable("delivery_attempts");

        entity.HasIndex(e => new { e.SubscriptionDeliveryId, e.AttemptNumber }, "idx_delivery_attempts_delivery");

        entity.HasIndex(e => new { e.SubscriptionDeliveryId, e.Id }, "uq_delivery_attempts_delivery_id").IsUnique();

        entity.HasIndex(e => new { e.SubscriptionDeliveryId, e.AttemptNumber }, "uq_delivery_attempts_delivery_number").IsUnique();

        entity.Property(e => e.Id)
            .HasDefaultValueSql("gen_random_uuid()")
            .HasColumnName("id");
        entity.Property(e => e.AttemptNumber).HasColumnName("attempt_number");
        entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
        entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
        entity.Property(e => e.FailurePhase).HasColumnName("failure_phase");
        entity.Property(e => e.RequestPayload)
            .HasColumnType("jsonb")
            .HasColumnName("request_payload");
        entity.Property(e => e.ResponseBody).HasColumnName("response_body");
        entity.Property(e => e.ResponseStatusCode).HasColumnName("response_status_code");
        entity.Property(e => e.StartedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("started_at");
        entity.Property(e => e.Status).HasColumnName("status");
        entity.Property(e => e.SubscriptionDeliveryId).HasColumnName("subscription_delivery_id");

        entity.HasOne<SubscriptionDelivery>().WithMany()
            .HasForeignKey(d => d.SubscriptionDeliveryId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("delivery_attempts_subscription_delivery_id_fkey");
    }
}
