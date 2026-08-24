using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Topics;

internal sealed class TopicSourceConfiguration : IEntityTypeConfiguration<TopicSource>
{
    public void Configure(EntityTypeBuilder<TopicSource> entity)
    {
        entity.ToTable("topic_sources", table =>
        {
            table.HasCheckConstraint("ck_topic_sources_status", "status IN ('active', 'inactive')");
            table.HasCheckConstraint(
                "ck_topic_sources_inactive_at",
                "((status = 'active' AND inactive_at IS NULL) "
                + "OR (status = 'inactive' AND inactive_at IS NOT NULL))");
        });

        entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        entity.Property(e => e.TopicId).HasColumnName("topic_id");

        entity.HasKey(e => new { e.TenantId, e.TopicId, e.ConnectionId })
            .HasName("topic_sources_pkey");

        entity.HasIndex(e => e.ConnectionId, "idx_topic_sources_connection_id");

        entity.HasIndex(e => e.TopicId).HasDatabaseName("idx_topic_sources_topic_id");

        entity.Property(e => e.ConnectionId).HasColumnName("connection_id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.InactiveAt).HasColumnName("inactive_at");
        entity.Property(e => e.Status)
            .IsRequired()
            .HasDefaultValueSql("'active'::text")
            .HasColumnName("status");

        entity.Ignore(e => e.Endpoint);

        entity.HasOne<Connection>().WithMany()
            .HasPrincipalKey(nameof(Connection.TenantId), nameof(Connection.Id))
            .HasForeignKey(e => new { e.TenantId, e.ConnectionId })
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("fk_topic_sources_connection_tenant");

        entity.HasOne<Topic>().WithMany()
            .HasPrincipalKey(nameof(Topic.TenantId), nameof(Topic.Id))
            .HasForeignKey(e => new { e.TenantId, e.TopicId })
            .HasConstraintName("fk_topic_sources_topic_tenant");
    }
}
