using Integrios.Domain.Connections;
using Integrios.Domain.Topics;
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

        entity.Property<Guid>("TenantId").HasColumnName("tenant_id");
        entity.Property<Guid>("TopicId").HasColumnName("topic_id");

        entity.HasKey("TenantId", "TopicId", nameof(TopicSource.ConnectionId)).HasName("topic_sources_pkey");

        entity.HasIndex(e => e.ConnectionId, "idx_topic_sources_connection_id");

        entity.HasIndex("TopicId").HasDatabaseName("idx_topic_sources_topic_id");

        entity.Property(e => e.ConnectionId).HasColumnName("connection_id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property<DateTimeOffset?>("InactiveAt").HasColumnName("inactive_at");
        entity.Property<string>("Status")
            .IsRequired()
            .HasDefaultValueSql("'active'::text")
            .HasColumnName("status");

        entity.Ignore(e => e.Endpoint);

        entity.HasOne<Connection>().WithMany()
            .HasPrincipalKey(nameof(Connection.TenantId), nameof(Connection.Id))
            .HasForeignKey("TenantId", nameof(TopicSource.ConnectionId))
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("fk_topic_sources_connection_tenant");

        entity.HasOne<Topic>().WithMany()
            .HasPrincipalKey(nameof(Topic.TenantId), nameof(Topic.Id))
            .HasForeignKey("TenantId", "TopicId")
            .HasConstraintName("fk_topic_sources_topic_tenant");
    }
}
