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

        // An already-accepted duplicate resolves by stable Source identity before the current
        // normalization revision is applied, and that lookup runs on the acceptance boundary of
        // every transport. Filtered, because only Events carrying an identity are ever looked up.
        entity.HasIndex(e => new { e.SourceId, e.SourceEventId }, "idx_events_source_event_id")
            .HasFilter("(source_event_id IS NOT NULL)");

        // Newest-first Tenant Event history keyset: (accepted_at, id) is the cursor tuple. Status is
        // included because Event activity classifies every Event in a window by it; without it SQL
        // Server scans the clustered index rather than look each windowed Event up.
        IndexBuilder<DomainEvent> history = entity.HasIndex(e => new { e.TenantId, e.AcceptedAt, e.Id }, "idx_events_tenant_accepted")
            .IsDescending(false, true, true);
        NpgsqlIndexBuilderExtensions.IncludeProperties(history, e => e.Status);
        SqlServerIndexBuilderExtensions.IncludeProperties(history, e => e.Status);

        // A Subscription previews its mapping against the newest Events of its own type on its
        // Topic. A rare type among busy ones would otherwise walk the whole Tenant history above.
        entity.HasIndex(e => new { e.TenantId, e.TopicId, e.EventType, e.AcceptedAt, e.Id }, "idx_events_topic_type_accepted")
            .IsDescending(false, false, false, true, true);

        // The monitoring backlog counts Events awaiting routing and unrouted however old, with the
        // oldest acceptance of each. Filtered to those two statuses, so it stays as small as the
        // backlog itself rather than scanning the Tenant's whole history. Status is only included,
        // not a key column: SQL Server stores it as nvarchar(max), which cannot be an index key.
        IndexBuilder<DomainEvent> backlog = entity.HasIndex(e => new { e.TenantId, e.AcceptedAt }, "idx_events_tenant_backlog")
            .HasFilter("(status IN ('accepted', 'unrouted'))");
        NpgsqlIndexBuilderExtensions.IncludeProperties(backlog, e => e.Status);
        SqlServerIndexBuilderExtensions.IncludeProperties(backlog, e => e.Status);

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
