using Integrios.Domain.Common;
using Integrios.Domain.Tenants;
using Integrios.Domain.Topics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Integrios.Infrastructure.Data.ModelConfigurationConversions;

namespace Integrios.Infrastructure.Topics;

internal sealed class TopicConfiguration : IEntityTypeConfiguration<Topic>
{
    public void Configure(EntityTypeBuilder<Topic> entity)
    {
        entity.HasKey(e => e.Id).HasName("pipelines_pkey");

        entity.ToTable("topics");

        entity.HasIndex(e => e.TenantId, "idx_topics_tenant_id");

        entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_topics_tenant_id_id").IsUnique();

        entity.HasIndex(e => new { e.TenantId, e.Name }, "uq_topics_tenant_name").IsUnique();

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Description).HasColumnName("description");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.Status)
            .HasConversion(value => ToSnakeCase(value), value => FromSnakeCase<OperationalStatus>(value))
            .HasDefaultValueSql("'active'::text")
            .HasColumnName("status");
        entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        entity.Property(e => e.UpdatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("updated_at");

        entity.Ignore(e => e.Sources);

        entity.HasOne<Tenant>().WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("pipelines_tenant_id_fkey");
    }
}
