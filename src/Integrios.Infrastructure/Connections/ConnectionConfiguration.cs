using Integrios.Domain.Common;
using Integrios.Domain.Connections;
using Integrios.Domain.Integrations;
using Integrios.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Integrios.Infrastructure.Data.ModelConfigurationConversions;

namespace Integrios.Infrastructure.Connections;

internal sealed class ConnectionConfiguration : IEntityTypeConfiguration<Connection>
{
    public void Configure(EntityTypeBuilder<Connection> entity)
    {
        entity.HasKey(e => e.Id).HasName("connections_pkey");

        entity.ToTable("connections");

        entity.HasIndex(e => e.TenantId, "idx_connections_tenant_id");

        entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_connections_tenant_id_id").IsUnique();

        entity.HasIndex(e => new { e.TenantId, e.Name }, "uq_connections_tenant_name").IsUnique();

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.Config)
            .HasDefaultValueSql("'{}'::jsonb")
            .HasColumnType("jsonb")
            .HasColumnName("config");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Description).HasColumnName("description");
        entity.Property(e => e.DestinationAuthentication)
            .HasConversion(
                value => SerializeJson(value),
                value => DeserializeJson<ConnectionSchemeSelection>(value))
            .HasColumnType("jsonb")
            .HasColumnName("destination_authentication");
        entity.Property(e => e.Environment).HasColumnName("environment");
        entity.Property(e => e.IntegrationId).HasColumnName("integration_id");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.SourceVerification)
            .HasConversion(
                value => SerializeJson(value),
                value => DeserializeJson<ConnectionSchemeSelection>(value))
            .HasColumnType("jsonb")
            .HasColumnName("source_verification");
        entity.Property(e => e.Status)
            .HasConversion(value => ToSnakeCase(value), value => FromSnakeCase<OperationalStatus>(value))
            .HasDefaultValueSql("'active'::text")
            .HasColumnName("status");
        entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        entity.Property(e => e.UpdatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("updated_at");

        entity.HasOne<Integration>().WithMany()
            .HasForeignKey(d => d.IntegrationId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("connections_integration_id_fkey");

        entity.HasOne<Tenant>().WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("connections_tenant_id_fkey");
    }
}
