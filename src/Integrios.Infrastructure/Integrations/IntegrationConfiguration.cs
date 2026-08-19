using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Integrations;

internal sealed class IntegrationConfiguration : IEntityTypeConfiguration<Integration>
{
    public void Configure(EntityTypeBuilder<Integration> entity)
    {
        entity.HasKey(e => e.Id).HasName("integrations_pkey");

        entity.ToTable("integrations", table =>
        {
            table.HasCheckConstraint("ck_integrations_contract_version_positive", "contract_version > 0");
            table.HasCheckConstraint(
                "ck_integrations_manifest_schema_version_positive",
                "manifest_schema_version > 0");
            table.HasCheckConstraint("ck_integrations_manifest_object", "jsonb_typeof(manifest) = 'object'");
            table.HasCheckConstraint(
                "ck_integrations_manifest_identity",
                "manifest->>'key' = key "
                + "AND (manifest->>'contract_version')::INTEGER = contract_version "
                + "AND (manifest->>'manifest_schema_version')::INTEGER = manifest_schema_version");
        });

        entity.HasAlternateKey(e => new { e.Key, e.ContractVersion })
            .HasName("uq_integrations_key_contract_version");

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.ContractVersion).HasColumnName("contract_version");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Description).HasColumnName("description");
        entity.Property(e => e.Direction).HasColumnName("direction");
        entity.Property(e => e.Key).HasColumnName("key");
        entity.Property(e => e.Manifest)
            .HasColumnType("jsonb")
            .HasColumnName("manifest");
        entity.Property(e => e.ManifestSchemaVersion).HasColumnName("manifest_schema_version");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.Status)
            .HasDefaultValueSql("'active'::text")
            .HasColumnName("status");
        entity.Property(e => e.SupportedAuthSchemes)
            .HasDefaultValueSql("'[]'::jsonb")
            .HasColumnType("jsonb")
            .HasColumnName("supported_auth_schemes");
        entity.Property(e => e.UpdatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("updated_at");
    }
}
