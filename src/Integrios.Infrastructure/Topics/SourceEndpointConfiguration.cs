using Integrios.Domain.Topics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Topics;

internal sealed class SourceEndpointConfiguration : IEntityTypeConfiguration<SourceEndpoint>
{
    public void Configure(EntityTypeBuilder<SourceEndpoint> entity)
    {
        entity.HasKey(e => e.Id).HasName("source_endpoints_pkey");

        entity.ToTable("source_endpoints", table =>
        {
            table.HasCheckConstraint(
                "ck_source_endpoints_status",
                "status IN ('active', 'revoked')");
            table.HasCheckConstraint(
                "ck_source_endpoints_revoked_at",
                "((status = 'active' AND revoked_at IS NULL) "
                + "OR (status = 'revoked' AND revoked_at IS NOT NULL))");
        });

        entity.Property<Guid>("TenantId").HasColumnName("tenant_id");
        entity.Property<Guid>("TopicId").HasColumnName("topic_id");
        entity.Property<Guid>("ConnectionId").HasColumnName("connection_id");
        entity.Property<string>("Status")
            .IsRequired()
            .HasDefaultValueSql("'active'::text")
            .HasColumnName("status");
        entity.Property<DateTimeOffset?>("RevokedAt").HasColumnName("revoked_at");

        entity.HasIndex("TenantId", "TopicId", "ConnectionId", nameof(SourceEndpoint.CreatedAt))
            .HasDatabaseName("idx_source_endpoints_association");

        entity.HasAlternateKey(e => e.CallbackPath).HasName("source_endpoints_callback_path_key");

        entity.HasIndex("TenantId", "TopicId", "ConnectionId")
            .HasDatabaseName("uq_source_endpoints_active_association")
            .IsUnique()
            .HasFilter("(status = 'active'::text)");

        entity.Property(e => e.Id)
            .HasDefaultValueSql("gen_random_uuid()")
            .HasColumnName("id");
        entity.Property(e => e.CallbackPath).HasColumnName("callback_path");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");

        entity.HasOne<TopicSource>().WithMany()
            .HasForeignKey("TenantId", "TopicId", "ConnectionId")
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("fk_source_endpoints_topic_source");
    }
}
