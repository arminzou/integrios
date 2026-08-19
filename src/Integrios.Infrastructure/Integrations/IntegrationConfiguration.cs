using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Integrios.Infrastructure.Data.ModelConfigurationConversions;

namespace Integrios.Infrastructure.Integrations;

internal sealed class IntegrationConfiguration : IEntityTypeConfiguration<Integration>
{
    public void Configure(EntityTypeBuilder<Integration> entity)
    {
        entity.HasKey(e => e.Id).HasName("integrations_pkey");

        entity.ToTable("integrations");

        entity.HasIndex(e => new { e.Key, e.ContractVersion }, "uq_integrations_key_contract_version").IsUnique();

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.ContractVersion).HasColumnName("contract_version");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Description).HasColumnName("description");
        entity.Property(e => e.Direction)
            .HasConversion(value => ToSnakeCase(value), value => FromSnakeCase<IntegrationDirection>(value))
            .HasColumnName("direction");
        entity.Property(e => e.Key).HasColumnName("key");
        entity.Property(e => e.Manifest)
            .HasConversion(value => SerializeJson(value), value => DeserializeJson<IntegrationManifest>(value))
            .HasColumnType("jsonb")
            .HasColumnName("manifest");
        entity.Property(e => e.ManifestSchemaVersion).HasColumnName("manifest_schema_version");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.Status)
            .HasConversion(value => ToSnakeCase(value), value => FromSnakeCase<OperationalStatus>(value))
            .HasDefaultValueSql("'active'::text")
            .HasColumnName("status");
        entity.Property(e => e.SupportedAuthSchemes)
            .HasConversion(
                value => SerializeJson(value),
                value => DeserializeJson<IReadOnlyList<string>>(value))
            .HasDefaultValueSql("'[]'::jsonb")
            .HasColumnType("jsonb")
            .HasColumnName("supported_auth_schemes");
        entity.Property(e => e.UpdatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("updated_at");
    }
}
