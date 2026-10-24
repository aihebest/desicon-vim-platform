using Desicon.Vim.Domain.VendorAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Vim.Infrastructure.Persistence.Configurations;

internal sealed class VendorInvitationConfiguration : IEntityTypeConfiguration<VendorInvitation>
{
    public void Configure(EntityTypeBuilder<VendorInvitation> builder)
    {
        builder.ToTable("VendorInvitations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.VendorNo).HasMaxLength(20).IsRequired();
        builder.Property(x => x.VendorName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(Len.Email).IsRequired();
        builder.Property(x => x.TokenHash).HasMaxLength(Len.Sha256Hex).IsRequired();
        builder.Property(x => x.Confirmation).HasConversion<string>().HasMaxLength(Len.EnumName);
        builder.Property(x => x.ConfirmationNote).HasMaxLength(Len.Remark);
        builder.Property(x => x.CreatedById).HasMaxLength(Len.EntraId).IsRequired();
        builder.Property(x => x.CreatedByName).HasMaxLength(Len.PersonName).IsRequired();
        builder.Property(x => x.AcceptedByUserId).HasMaxLength(Len.ExternalUserId);
        builder.Property(x => x.RevokedById).HasMaxLength(Len.EntraId);
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => new { x.VendorNo, x.Email });
    }
}

internal sealed class VendorUserConfiguration : IEntityTypeConfiguration<VendorUser>
{
    public void Configure(EntityTypeBuilder<VendorUser> builder)
    {
        builder.ToTable("VendorUsers");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ExternalUserId).HasMaxLength(Len.ExternalUserId).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(Len.Email).IsRequired();
        builder.Property(x => x.VendorNo).HasMaxLength(20).IsRequired();
        builder.Property(x => x.VendorName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.DisabledById).HasMaxLength(Len.EntraId);
        builder.Property(x => x.DisabledReason).HasMaxLength(Len.Reason);
        builder.Ignore(x => x.IsDisabled);
        builder.HasIndex(x => x.ExternalUserId).IsUnique();
        builder.HasIndex(x => x.VendorNo);
    }
}

/// <summary>Append-only (the app identities are denied UPDATE/DELETE, like AuditEvents).</summary>
internal sealed class VendorAccessEventConfiguration : IEntityTypeConfiguration<VendorAccessEvent>
{
    public void Configure(EntityTypeBuilder<VendorAccessEvent> builder)
    {
        builder.ToTable("VendorAccessEvents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Action).HasMaxLength(40).IsRequired();
        builder.Property(x => x.VendorNo).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(Len.Email).IsRequired();
        builder.Property(x => x.ActorId).HasMaxLength(Len.ExternalUserId).IsRequired();
        builder.Property(x => x.ActorName).HasMaxLength(400).IsRequired();
        builder.Property(x => x.Details).HasMaxLength(Len.Reason);
        builder.HasIndex(x => new { x.VendorNo, x.OccurredAtUtc });
    }
}
