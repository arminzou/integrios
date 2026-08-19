using Integrios.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using DomainEvent = Integrios.Domain.Events.Event;

namespace Integrios.Infrastructure.Outbox;

internal sealed class OutboxEntryConfiguration : IEntityTypeConfiguration<OutboxEntry>
{
    public void Configure(EntityTypeBuilder<OutboxEntry> entity)
    {
        entity.HasKey(e => e.Id).HasName("outbox_pkey");

        entity.ToTable("outbox");

        entity.HasIndex(e => new { e.DeliverAfter, e.CreatedAt }, "idx_outbox_pending")
            .HasFilter("(processed_at IS NULL)")
            .HasNullSortOrder(new[] { NullSortOrder.NullsFirst, NullSortOrder.NullsLast });

        entity.Property(e => e.Id)
            .HasDefaultValueSql("gen_random_uuid()")
            .HasColumnName("id");
        entity.Property(e => e.AttemptCount).HasColumnName("attempt_count");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.DeliverAfter).HasColumnName("deliver_after");
        entity.Property(e => e.EventId).HasColumnName("event_id");
        entity.Property(e => e.Payload)
            .HasColumnType("jsonb")
            .HasColumnName("payload");
        entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
        entity.Property(e => e.Traceparent).HasColumnName("traceparent");

        entity.HasOne<DomainEvent>().WithMany()
            .HasForeignKey(d => d.EventId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("outbox_event_id_fkey");
    }
}
