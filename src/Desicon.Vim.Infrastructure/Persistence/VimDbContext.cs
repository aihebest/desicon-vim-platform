using Desicon.Vim.Domain.Audit;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Vim.Infrastructure.Persistence;

/// <summary>
/// The VIM database (Azure SQL, schema "vim"). Table layouts live in the
/// IEntityTypeConfiguration classes under Persistence/Configurations.
/// Always save through <see cref="EfUnitOfWork"/> so audit events are written.
/// </summary>
public sealed class VimDbContext(DbContextOptions<VimDbContext> options) : DbContext(options)
{
    public const string Schema = "vim";

    /// <summary>SQL SEQUENCE used for VIM-yyyy-nnnnnn references.</summary>
    public const string CaseReferenceSequence = "CaseReferenceSeq";

    /// <summary>
    /// Shadow column on Cases: DuplicateDetector.NormaliseKey(VendorName).
    /// The normalisation is not translatable to SQL, so EfUnitOfWork stores it at save time.
    /// </summary>
    public const string VendorNameKeyColumn = "VendorNameKey";

    /// <summary>Shadow column on Cases: DuplicateDetector.NormaliseInvoiceNumber(InvoiceNumber), stored at save time.</summary>
    public const string InvoiceNumberKeyColumn = "InvoiceNumberKey";

    public DbSet<InvoiceCase> Cases => Set<InvoiceCase>();

    public DbSet<CaseDocument> CaseDocuments => Set<CaseDocument>();

    public DbSet<CaseChecklistAnswer> CaseChecklistAnswers => Set<CaseChecklistAnswer>();

    public DbSet<CaseFlag> CaseFlags => Set<CaseFlag>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<CategoryRoute> CategoryRoutes => Set<CategoryRoute>();

    public DbSet<RouteChangeRecord> RouteChanges => Set<RouteChangeRecord>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboundMessage> InboundMessages => Set<InboundMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasSequence<long>(CaseReferenceSequence, Schema)
            .StartsAt(1)
            .IncrementsBy(1);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VimDbContext).Assembly);
    }
}
