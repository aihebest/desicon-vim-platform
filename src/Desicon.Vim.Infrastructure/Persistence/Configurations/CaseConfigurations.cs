using Desicon.Vim.Domain.Cases;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desicon.Vim.Infrastructure.Persistence.Configurations;

/// <summary>Column sizes shared across tables, so the same kind of value always has the same size.</summary>
internal static class Len
{
    public const int EntraId = 100;
    public const int PersonName = 200;
    public const int Email = 320;
    public const int EnumName = 32;
    public const int Reference = 32;
    public const int Sha256Hex = 64;
    public const int Reason = 2000;
    public const int Remark = 1000;
}

internal sealed class InvoiceCaseConfiguration : IEntityTypeConfiguration<InvoiceCase>
{
    private static readonly string[] VendorNameKeyIndex = [VimDbContext.VendorNameKeyColumn];
    private static readonly string[] InvoiceNumberKeyIndex = [VimDbContext.InvoiceNumberKeyColumn];

    public void Configure(EntityTypeBuilder<InvoiceCase> builder)
    {
        builder.ToTable("Cases");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Reference).HasMaxLength(Len.Reference).IsRequired();
        builder.HasIndex(x => x.Reference).IsUnique();

        builder.Property(x => x.CaseType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(Len.EnumName);
        builder.Property(x => x.Channel).HasConversion<string>().HasMaxLength(Len.EnumName);
        builder.Property(x => x.SupplyType).HasConversion<string>().HasMaxLength(Len.EnumName);

        builder.Property(x => x.SenderEmail).HasMaxLength(Len.Email);
        builder.Property(x => x.SenderName).HasMaxLength(Len.PersonName);
        builder.Property(x => x.SubmissionSubject).HasMaxLength(500);

        builder.Property(x => x.VendorName).HasMaxLength(200);
        builder.Property(x => x.VendorNo).HasMaxLength(20);
        builder.Property(x => x.InvoiceNumber).HasMaxLength(100);
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        builder.Property(x => x.OrderNumber).HasMaxLength(50);
        builder.Property(x => x.CategoryCode).HasMaxLength(50);
        builder.Property(x => x.DeliveredOn);
        builder.Property(x => x.AcceptedAtUtc);
        builder.Property(x => x.ExtractedJson);

        builder.Property(x => x.RegisteredById).HasMaxLength(Len.EntraId);
        builder.Property(x => x.AssignedReviewerId).HasMaxLength(Len.EntraId);
        builder.Property(x => x.AssignedReviewerName).HasMaxLength(Len.PersonName);
        builder.Property(x => x.ReviewerDelegateIds).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.VerifiedById).HasMaxLength(Len.EntraId);
        builder.Property(x => x.ApProcessedById).HasMaxLength(Len.EntraId);

        builder.Property(x => x.ErpDocumentNumber).HasMaxLength(50);
        builder.Property(x => x.PaymentReference).HasMaxLength(100);
        builder.Property(x => x.LastReason).HasMaxLength(Len.Reason);

        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.Property(x => x.LastAuditHash).HasMaxLength(Len.Sha256Hex).IsRequired();

        // Normalised keys for duplicate checks; set by EfUnitOfWork before every save.
        builder.Property<string>(VimDbContext.VendorNameKeyColumn).HasMaxLength(200).IsRequired().HasDefaultValue(string.Empty);
        builder.Property<string>(VimDbContext.InvoiceNumberKeyColumn).HasMaxLength(100).IsRequired().HasDefaultValue(string.Empty);

        builder.HasIndex(x => new { x.Status, x.DueAtUtc });
        builder.HasIndex(x => x.VendorName);
        builder.HasIndex(x => x.InvoiceNumber);
        builder.HasIndex(x => new { x.AssignedReviewerId, x.Status });
        builder.HasIndex(x => x.VendorNo);
        builder.HasIndex(VendorNameKeyIndex);
        builder.HasIndex(InvoiceNumberKeyIndex);

        // Child collections are private lists inside the aggregate; EF reads and writes the fields.
        builder.HasMany(x => x.Documents).WithOne().HasForeignKey(d => d.CaseId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Documents).HasField("_documents").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(x => x.ChecklistAnswers).WithOne().HasForeignKey(a => a.CaseId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.ChecklistAnswers).HasField("_checklistAnswers").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(x => x.Flags).WithOne().HasForeignKey(f => f.CaseId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Flags).HasField("_flags").UsePropertyAccessMode(PropertyAccessMode.Field);

        // Computed / transient members. Audit events are saved to their own table by EfUnitOfWork.
        builder.Ignore(x => x.NewAuditEvents);
        builder.Ignore(x => x.HasOpenBlockingFlags);
        builder.Ignore(x => x.DelegateIdList);
        builder.Ignore(x => x.CurrentDocuments);
        builder.Ignore(x => x.MissingRequiredDocuments);
        builder.Ignore(x => x.AllCurrentDocumentsScanned);
        builder.Ignore(x => x.BlockingFlagResolverIds);
    }
}

internal sealed class CaseDocumentConfiguration : IEntityTypeConfiguration<CaseDocument>
{
    public void Configure(EntityTypeBuilder<CaseDocument> builder)
    {
        builder.ToTable("CaseDocuments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(Len.EnumName);
        builder.Property(x => x.FileName).HasMaxLength(260).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Sha256).HasMaxLength(Len.Sha256Hex).IsRequired();
        builder.Property(x => x.BlobPath).HasMaxLength(600).IsRequired();
        builder.Property(x => x.AddedById).HasMaxLength(Len.EntraId).IsRequired();
        builder.Property(x => x.ScanStatus).HasConversion<string>().HasMaxLength(Len.EnumName);
        builder.HasIndex(x => x.Sha256);
    }
}

internal sealed class CaseChecklistAnswerConfiguration : IEntityTypeConfiguration<CaseChecklistAnswer>
{
    public void Configure(EntityTypeBuilder<CaseChecklistAnswer> builder)
    {
        builder.ToTable("CaseChecklistAnswers");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ChecklistVersion).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Stage).HasConversion<string>().HasMaxLength(Len.EnumName);
        builder.Property(x => x.ItemCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Answer).HasConversion<string>().HasMaxLength(Len.EnumName);
        builder.Property(x => x.Remark).HasMaxLength(Len.Remark);
        builder.Property(x => x.AnsweredById).HasMaxLength(Len.EntraId).IsRequired();
    }
}

internal sealed class CaseFlagConfiguration : IEntityTypeConfiguration<CaseFlag>
{
    public void Configure(EntityTypeBuilder<CaseFlag> builder)
    {
        builder.ToTable("CaseFlags");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Code).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(Len.Remark).IsRequired();
        builder.Property(x => x.RaisedById).HasMaxLength(Len.EntraId).IsRequired();
        builder.Property(x => x.ResolvedById).HasMaxLength(Len.EntraId);
        builder.Property(x => x.Resolution).HasMaxLength(Len.Remark);
        builder.Ignore(x => x.IsOpen);
        builder.HasIndex(x => new { x.CaseId, x.ResolvedAtUtc });
    }
}
