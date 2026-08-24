using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Sources;

internal sealed class SourceConfiguration : IEntityTypeConfiguration<Source>
{
    public void Configure(EntityTypeBuilder<Source> entity)
    {
        entity.HasKey(source => source.Id).HasName("sources_pkey");
        entity.ToTable("sources", table =>
        {
            table.HasCheckConstraint("ck_sources_type", "type IN ('event_api', 'webhook', 'queue')");
            table.HasCheckConstraint("ck_sources_status", "status IN ('active', 'revoked')");
            table.HasCheckConstraint("ck_sources_revoked_at", "((status = 'active' AND revoked_at IS NULL) OR (status = 'revoked' AND revoked_at IS NOT NULL))");
        });

        entity.HasAlternateKey(source => new { source.TenantId, source.Id }).HasName("uq_sources_tenant_id_id");
        entity.HasIndex(source => new { source.TenantId, source.CreatedAt, source.Id }, "idx_sources_tenant_created");
        entity.HasIndex(source => source.ConnectionId, "idx_sources_connection_id");
        entity.HasIndex(source => source.TopicId, "idx_sources_topic_id");
        entity.Property(source => source.Id).ValueGeneratedNever().HasColumnName("id");
        entity.Property(source => source.TenantId).HasColumnName("tenant_id");
        entity.Property(source => source.ConnectionId).HasColumnName("connection_id");
        entity.Property(source => source.TopicId).HasColumnName("topic_id");
        entity.Property(source => source.Type).HasColumnName("type");
        entity.Property(source => source.Configuration).HasColumnType("jsonb").HasColumnName("configuration");
        entity.Property(source => source.Status).HasColumnName("status").HasDefaultValueSql("'active'::text");
        entity.Property(source => source.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        entity.Property(source => source.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        entity.Property(source => source.RevokedAt).HasColumnName("revoked_at");

        entity.HasOne<Tenant>().WithMany().HasForeignKey(source => source.TenantId)
            .OnDelete(DeleteBehavior.ClientSetNull).HasConstraintName("fk_sources_tenant");
        entity.HasOne<Connection>().WithMany().HasPrincipalKey(connection => new { connection.TenantId, connection.Id })
            .HasForeignKey(source => new { source.TenantId, source.ConnectionId })
            .OnDelete(DeleteBehavior.ClientSetNull).HasConstraintName("fk_sources_connection_tenant");
        entity.HasOne<Topic>().WithMany().HasPrincipalKey(topic => new { topic.TenantId, topic.Id })
            .HasForeignKey(source => new { source.TenantId, source.TopicId })
            .OnDelete(DeleteBehavior.ClientSetNull).HasConstraintName("fk_sources_topic_tenant");
    }
}
