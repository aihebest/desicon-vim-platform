using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Application.Queries;
using Desicon.Vim.Domain.Audit;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Integrity;
using Desicon.Vim.Domain.Workflow;

namespace Desicon.Vim.Application.Abstractions;

// These interfaces ("ports") describe what the application needs from the
// outside world. Desicon.Vim.Infrastructure implements them with Azure SQL,
// Blob Storage, Microsoft Graph, Document Intelligence and Business Central.
// Tests implement them in memory.

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface ICaseRepository
{
    Task<InvoiceCase?> GetAsync(Guid id, CancellationToken ct);

    Task<InvoiceCase?> GetByReferenceAsync(string reference, CancellationToken ct);

    void Add(InvoiceCase invoiceCase);

    /// <summary>
    /// Candidate duplicates, excluding cancelled cases: same BC vendor number, OR same
    /// normalised vendor name, OR same normalised invoice number (any vendor).
    /// </summary>
    Task<IReadOnlyList<InvoiceFingerprint>> GetFingerprintsAsync(string? vendorNo, string vendorNameKey, string invoiceNumberKey, CancellationToken ct);

    /// <summary>Open cases with at least one document still waiting for its malware scan result.</summary>
    Task<IReadOnlyList<InvoiceCase>> GetWithPendingScansAsync(CancellationToken ct);

    /// <summary>Portal / assisted submissions waiting for their automatic checks (status Received).</summary>
    Task<IReadOnlyList<InvoiceCase>> GetAwaitingChecksAsync(CancellationToken ct);

    /// <summary>
    /// Other cases already claiming value on this PO / SO that Business Central does not
    /// yet show as invoiced (submitted, with the FM or Accounts, or accepted).
    /// </summary>
    Task<IReadOnlyList<(string Reference, decimal Amount)>> GetOpenClaimsOnOrderAsync(string vendorNo, string orderNumber, Guid excludeCaseId, CancellationToken ct);

    /// <summary>Cases with Accounts or the Finance Manager whose half-way reminder is due.</summary>
    Task<IReadOnlyList<InvoiceCase>> GetDueForReminderAsync(DateTimeOffset nowUtc, CancellationToken ct);

    Task<IReadOnlyList<InvoiceCase>> GetOverdueNotEscalatedAsync(DateTimeOffset nowUtc, CancellationToken ct);

    Task<IReadOnlyList<InvoiceCase>> GetAwaitingPaymentAsync(CancellationToken ct);
}

public interface ICaseQueries
{
    Task<PagedResult<CaseSummary>> SearchAsync(CaseSearch search, CancellationToken ct);

    Task<IReadOnlyList<AuditEvent>> GetAuditTrailAsync(Guid caseId, CancellationToken ct);

    Task<DashboardFigures> GetDashboardAsync(DateTimeOffset nowUtc, CancellationToken ct);
}

/// <summary>Saves all changes from one operation in a single transaction (case, audit events, outbox).</summary>
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Forgets unsaved changes (after a failure), so a later save in the same scope does not resend them.</summary>
    void DiscardChanges();
}

public interface IReferenceGenerator
{
    Task<string> NextAsync(int year, CancellationToken ct);
}

public interface IRouteRepository
{
    Task<CategoryRoute?> GetAsync(string code, CancellationToken ct);

    Task<IReadOnlyList<CategoryRoute>> ListAsync(CancellationToken ct);

    void Add(CategoryRoute route);

    /// <summary>Insert-only history of routing changes.</summary>
    void AddChange(RouteChangeRecord change);
}

/// <summary>Records which inbound emails were already processed, so a retry never creates a second case.</summary>
public interface IInboundMessageLog
{
    Task<bool> HasProcessedAsync(string messageId, CancellationToken ct);

    void MarkProcessed(string messageId, string? caseReference, string outcome, DateTimeOffset atUtc);
}

public sealed record StoredDocument(string BlobPath, string Sha256, long SizeBytes);

public interface IDocumentStore
{
    Task<StoredDocument> UploadAsync(string caseReference, int round, string fileName, string contentType, Stream content, CancellationToken ct);

    Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct);
}

/// <summary>Reads the result Microsoft Defender for Storage wrote for a stored file.</summary>
public interface IDocumentScanReader
{
    Task<ScanStatus> GetScanStatusAsync(string blobPath, CancellationToken ct);
}

public interface IInvoiceExtractor
{
    /// <summary>Reads key fields from an invoice PDF. Returns null if extraction is unavailable or failed.</summary>
    Task<ExtractedInvoiceFields?> ExtractAsync(Stream pdf, CancellationToken ct);
}

public sealed record ErpVendor(string VendorNo, string Name, bool Blocked, IReadOnlyList<string> Emails, IReadOnlyList<string> BankAccountNumbers);

/// <param name="RemainingAmount">Ordered but not yet invoiced, including VAT.</param>
/// <param name="ReceivedNotInvoicedAmount">Received (GRV / TRV posted) but not yet invoiced, including VAT: what a full-value invoice must equal.</param>
public sealed record ErpOrderCheck(
    bool OrderFound,
    bool BelongsToVendor,
    bool IsOpen,
    bool HasPostedReceipt,
    decimal? RemainingAmount,
    string? Currency,
    decimal? ReceivedNotInvoicedAmount = null);

public sealed record ErpPaymentStatus(bool Paid, string? PaymentReference, DateOnly? PaidOn);

/// <summary>Business Central, read-only in Phase 2. The platform never posts or pays.</summary>
public interface IErpGateway
{
    bool IsEnabled { get; }

    Task<ErpVendor?> GetVendorAsync(string vendorNo, CancellationToken ct);

    Task<IReadOnlyList<ErpVendor>> FindVendorsByEmailAsync(string email, CancellationToken ct);

    Task<ErpOrderCheck> CheckOrderAsync(string vendorNo, string orderNumber, CancellationToken ct);

    Task<ErpPaymentStatus?> GetPaymentStatusAsync(string erpDocumentNumber, CancellationToken ct);
}

public interface INotificationOutbox
{
    void Enqueue(OutboundNotification notification);
}
