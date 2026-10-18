using Desicon.Vim.Domain.Audit;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Vim.Infrastructure.Persistence.Configurations;

/// <summary>
/// Insert-only history. The FK to Cases uses NO ACTION (never cascade) so a
/// case delete cannot silently remove its history. The app's SQL identity is
/// additionally denied UPDATE/DELETE on this table (see docs/operations/database.md).
/// </summary>
internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasOne<InvoiceCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(x => new { x.CaseId, x.Sequence }).IsUnique();
        builder.HasIndex(x => new { x.Action, x.OccurredAtUtc });

        builder.Property(x => x.Action).HasMaxLength(50).IsRequired();
        builder.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(Len.EnumName);
        builder.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(Len.EnumName);
        builder.Property(x => x.ActorId).HasMaxLength(Len.EntraId).IsRequired();
        builder.Property(x => x.ActorName).HasMaxLength(Len.PersonName).IsRequired();
        builder.Property(x => x.ActorRoles).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(Len.Reason);
        builder.Property(x => x.DetailsJson);
        builder.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.PreviousHash).HasMaxLength(Len.Sha256Hex).IsRequired();
        builder.Property(x => x.Hash).HasMaxLength(Len.Sha256Hex).IsRequired();
    }
}

internal sealed class CategoryRouteConfiguration : IEntityTypeConfiguration<CategoryRoute>
{
    public void Configure(EntityTypeBuilder<CategoryRoute> builder)
    {
        builder.ToTable("CategoryRoutes");
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(50).ValueGeneratedNever();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ReviewerId).HasMaxLength(Len.EntraId).IsRequired();
        builder.Property(x => x.ReviewerName).HasMaxLength(Len.PersonName).IsRequired();
        builder.Property(x => x.DelegateIds).HasMaxLength(1000).IsRequired();
        builder.Ignore(x => x.DelegateIdList);
    }
}

/// <summary>Insert-only history of routing changes (app identities are denied UPDATE/DELETE, like AuditEvents).</summary>
internal sealed class RouteChangeRecordConfiguration : IEntityTypeConfiguration<RouteChangeRecord>
{
    public void Configure(EntityTypeBuilder<RouteChangeRecord> builder)
    {
        builder.ToTable("RouteChanges");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.RouteCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ChangedById).HasMaxLength(Len.EntraId).IsRequired();
        builder.Property(x => x.ChangedByName).HasMaxLength(Len.PersonName).IsRequired();
        builder.Property(x => x.OldValueJson).IsRequired();
        builder.Property(x => x.NewValueJson).IsRequired();
        builder.HasIndex(x => new { x.RouteCode, x.ChangedAtUtc });
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Template).HasMaxLength(60).IsRequired();
        builder.Property(x => x.Channel).HasConversion<string>().HasMaxLength(Len.EnumName);
        builder.Property(x => x.ToAddress).HasMaxLength(Len.Email).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(500).IsRequired();
        builder.Property(x => x.HtmlBody).IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(Len.Reason);
        builder.HasIndex(x => new { x.SentAtUtc, x.NextAttemptAtUtc });
        builder.HasIndex(x => x.CaseId);
    }
}

internal sealed class InboundMessageConfiguration : IEntityTypeConfiguration<InboundMessage>
{
    public void Configure(EntityTypeBuilder<InboundMessage> builder)
    {
        builder.ToTable("InboundMessages");
        builder.HasKey(x => x.MessageId);
        builder.Property(x => x.MessageId).HasMaxLength(EfInboundMessageLog.MaxKeyLength).ValueGeneratedNever();
        builder.Property(x => x.CaseReference).HasMaxLength(Len.Reference);
        builder.Property(x => x.Outcome).HasMaxLength(40).IsRequired();
    }
}
