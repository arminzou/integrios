using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Destinations;

internal sealed class DestinationConfiguration : IEntityTypeConfiguration<Destination>
{
    public void Configure(EntityTypeBuilder<Destination> entity)
    {
        entity.HasKey(e => e.Id).HasName("destinations_pkey");

        entity.ToTable("destinations", table =>
        {
            table.HasCheckConstraint(
                "ck_destinations_configuration_json",
                "jsonb_typeof(configuration) = 'object'");
            table.HasCheckConstraint(
                "ck_destinations_authentication_object",
                "authentication IS NULL OR jsonb_typeof(authentication) = 'object'");
        });

        entity.HasIndex(e => e.TenantId, "idx_destinations_tenant_id");

        entity.HasAlternateKey(e => new { e.TenantId, e.Id }).HasName("uq_destinations_tenant_id_id");

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.Configuration)
            .HasDefaultValueSql("'{}'::jsonb")
            .HasColumnType("jsonb")
            .HasColumnName("configuration");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Description).HasColumnName("description");
        entity.Property(e => e.Authentication)
            .HasColumnType("jsonb")
            .HasColumnName("authentication");
        entity.Property(e => e.Environment).HasColumnName("environment");
        entity.Property(e => e.ConnectorId).HasColumnName("connector_id");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.Status)
            .HasDefaultValueSql("'active'::text")
            .HasColumnName("status");
        entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        entity.Property(e => e.UpdatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("updated_at");

        entity.HasOne<Connector>().WithMany()
            .HasForeignKey(d => d.ConnectorId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("destinations_connector_id_fkey");

        entity.HasOne<Tenant>().WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("destinations_tenant_id_fkey");
    }
}
