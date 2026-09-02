using Integrios.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Integrios.Infrastructure.Identity;

internal sealed class OperatorIdentityConfiguration : IEntityTypeConfiguration<OperatorIdentity>
{
    public void Configure(EntityTypeBuilder<OperatorIdentity> entity)
    {
        entity.HasKey(e => e.Id).HasName("operator_identities_pkey");

        entity.ToTable("operator_identities");

        // The provider-qualified pair is the identity. This unique constraint is what makes
        // just-in-time provisioning safe under concurrent first sign-ins.
        entity.HasIndex(e => new { e.Issuer, e.Subject }, "uq_operator_identities_issuer_subject")
            .IsUnique();

        entity.Property(e => e.Id)
            .ValueGeneratedNever()
            .HasColumnName("id");
        entity.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()")
            .HasColumnName("created_at");
        entity.Property(e => e.Issuer).HasColumnName("issuer");
        entity.Property(e => e.Subject).HasColumnName("subject");
        entity.Property(e => e.UserId).HasColumnName("user_id");

        entity.HasOne<User>().WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("operator_identities_user_id_fkey");
    }
}
