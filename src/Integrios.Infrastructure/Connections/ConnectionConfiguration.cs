using Integrios.Domain.Common;
using Integrios.Domain.Connections;
using Integrios.Domain.Integrations;
using Integrios.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Connections;

internal sealed class ConnectionConfiguration : IEntityTypeConfiguration<Connection>
{
    public void Configure(EntityTypeBuilder<Connection> entity)
    {
        entity.HasKey(e => e.Id).HasName("connections_pkey");

        entity.ToTable("connections", table =>
        {
            table.HasCheckConstraint(
                "ck_connections_source_verification_object",
                "source_verification IS NULL OR jsonb_typeof(source_verification) = 'object'");
            table.HasCheckConstraint(
                "ck_connections_destination_authentication_object",
                "destination_authentication IS NULL OR jsonb_typeof(destination_authentication) = 'object'");
        });

        entity.HasIndex(e => e.TenantId, "idx_connections_tenant_id");

        entity.HasAlternateKey(e => new { e.TenantId, e.Id }).HasName("uq_connections_tenant_id_id");

        entity.HasAlternateKey(e => new { e.TenantId, e.Name }).HasName("uq_connections_tenant_name");

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
            .HasColumnType("jsonb")
            .HasColumnName("destination_authentication");
        entity.Property(e => e.Environment).HasColumnName("environment");
        entity.Property(e => e.IntegrationId).HasColumnName("integration_id");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.SourceVerification)
            .HasColumnType("jsonb")
            .HasColumnName("source_verification");
        entity.Property(e => e.Status)
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
