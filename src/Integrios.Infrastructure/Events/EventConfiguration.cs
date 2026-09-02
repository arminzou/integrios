using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainEvent = Integrios.Domain.Entities.Event;

namespace Integrios.Infrastructure.Events;

internal sealed class EventConfiguration : IEntityTypeConfiguration<DomainEvent>
{
    public void Configure(EntityTypeBuilder<DomainEvent> entity)
    {
        entity.HasKey(e => e.Id).HasName("events_pkey");

        entity.ToTable("events");

        entity.HasIndex(e => new { e.TenantId, e.IdempotencyKey }, "idx_events_idempotency")
            .IsUnique()
            .HasFilter("(idempotency_key IS NOT NULL)");

        // Newest-first Tenant Event history keyset: (accepted_at, id) is the cursor tuple.
        entity.HasIndex(e => new { e.TenantId, e.AcceptedAt, e.Id }, "idx_events_tenant_accepted")
            .IsDescending(false, true, true);

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.AcceptedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("accepted_at");
        entity.Property(e => e.EventType).HasColumnName("event_type");
        entity.Property(e => e.FailedAt).HasColumnName("failed_at");
        entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key");
        entity.Property(e => e.Metadata)
            .HasColumnType("jsonb")
            .HasColumnName("metadata");
        entity.Property(e => e.Payload)
            .HasColumnType("jsonb")
            .HasColumnName("payload");
        entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
        entity.Property(e => e.SourceId).HasColumnName("source_id");
        entity.Property(e => e.SourceEventId).HasColumnName("source_event_id");
        entity.Property(e => e.Status)
            .HasConversion(value => EventStatusMap.ToDbValue(value), value => EventStatusMap.FromDbValue(value))
            .HasDefaultValueSql("'accepted'::text")
            .HasColumnName("status");
        entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        entity.Property(e => e.TopicId).HasColumnName("topic_id");

        entity.HasOne<Tenant>().WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("events_tenant_id_fkey");

        entity.HasOne<Source>().WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(d => new { d.TenantId, d.SourceId })
            .HasConstraintName("fk_events_source_tenant");

        entity.HasOne<Topic>().WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(d => new { d.TenantId, d.TopicId })
            .HasConstraintName("fk_events_topic_tenant");

    }
}
