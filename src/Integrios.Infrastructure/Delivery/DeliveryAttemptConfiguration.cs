using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Delivery;

internal sealed class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<DeliveryAttempt> entity)
    {
        entity.HasKey(e => e.Id).HasName("delivery_attempts_pkey");

        entity.ToTable("delivery_attempts", table =>
        {
            table.HasCheckConstraint(
                "ck_delivery_attempts_status",
                "status IN ('in_progress', 'succeeded', 'failed', 'indeterminate')");
            table.HasCheckConstraint("ck_delivery_attempts_number_positive", "attempt_number > 0");
            table.HasCheckConstraint(
                "ck_delivery_attempts_failure_phase",
                "((status = 'failed' AND failure_phase IS NOT NULL "
                + "AND failure_phase IN ('transform', 'secret_resolution', 'request_construction', 'http')) "
                + "OR (status <> 'failed' AND failure_phase IS NULL))");
            table.HasCheckConstraint(
                "ck_delivery_attempts_completion",
                "((status = 'in_progress' AND completed_at IS NULL) "
                + "OR (status <> 'in_progress' AND completed_at IS NOT NULL))");
        });

        entity.HasIndex(e => new { e.EventDeliveryId, e.AttemptNumber }, "idx_delivery_attempts_delivery");

        entity.HasAlternateKey(e => new { e.EventDeliveryId, e.Id })
            .HasName("uq_delivery_attempts_delivery_id");

        entity.HasAlternateKey(e => new { e.EventDeliveryId, e.AttemptNumber })
            .HasName("uq_delivery_attempts_delivery_number");

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
        entity.Property(e => e.EventDeliveryId).HasColumnName("event_delivery_id");

        entity.HasOne<EventDelivery>().WithMany()
            .HasForeignKey(d => d.EventDeliveryId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("delivery_attempts_event_delivery_id_fkey");
    }
}
