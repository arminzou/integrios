using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.OperatorKeys;

internal sealed class OperatorKeyConfiguration : IEntityTypeConfiguration<OperatorKey>
{
    public void Configure(EntityTypeBuilder<OperatorKey> entity)
    {
        entity.HasKey(e => e.Id).HasName("operator_keys_pkey");

        entity.ToTable("operator_keys");

        entity.HasAlternateKey(e => e.PublicKey).HasName("operator_keys_public_key_key");

        entity.HasIndex(e => e.PublicKey, "idx_operator_keys_lookup").HasFilter("(revoked_at IS NULL)");

        entity.Property(e => e.Id)
            .HasDefaultValueSql("gen_random_uuid()")
            .HasColumnName("id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.PublicKey).HasColumnName("public_key");
        entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
        entity.Property(e => e.SecretHash).HasColumnName("secret_hash");
    }
}
