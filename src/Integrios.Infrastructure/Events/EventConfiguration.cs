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

        entity.HasIndex(e => e.TenantId, "idx_events_tenant_id");

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
        entity.Property(e => e.SourceConnectionId).HasColumnName("source_connection_id");
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

        entity.HasOne<Connection>().WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(d => new { d.TenantId, d.SourceConnectionId })
            .HasConstraintName("fk_events_source_connection_tenant");

        entity.HasOne<Topic>().WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(d => new { d.TenantId, d.TopicId })
            .HasConstraintName("fk_events_topic_tenant");

        entity.HasOne<TopicSource>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.TopicId, d.SourceConnectionId })
            .HasConstraintName("fk_events_topic_source");
    }
}
