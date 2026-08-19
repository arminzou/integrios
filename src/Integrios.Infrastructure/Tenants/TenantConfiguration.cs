using Integrios.Domain.Common;
using Integrios.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Tenants;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> entity)
    {
        entity.HasKey(e => e.Id).HasName("tenants_pkey");

        entity.ToTable("tenants");

        entity.HasIndex(e => e.Slug, "tenants_slug_key").IsUnique();

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Description).HasColumnName("description");
        entity.Property(e => e.Environment).HasColumnName("environment");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.Slug).HasColumnName("slug");
        entity.Property(e => e.Status)
            .HasDefaultValueSql("'active'::text")
            .HasColumnName("status");
        entity.Property(e => e.UpdatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("updated_at");
    }
}
