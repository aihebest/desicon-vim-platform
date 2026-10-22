using System.Security.Cryptography;
using Desicon.Vim.Application;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Integrity;
using Desicon.Vim.Domain.Workflow;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Application.Tests;

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

internal sealed class InMemoryStore : ICaseRepository, IRouteRepository, IInboundMessageLog, IReferenceGenerator, INotificationOutbox, IUnitOfWork, IDocumentStore
{
    private long _seq;

    public List<InvoiceCase> Cases { get; } = [];

    public List<CategoryRoute> Routes { get; } = [];

    public Dictionary<string, string> Processed { get; } = [];

    public List<OutboundNotification> Sent { get; } = [];

    public Dictionary<string, byte[]> Blobs { get; } = [];

    public int Saves { get; private set; }

    // ICaseRepository
    public Task<InvoiceCase?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Cases.SingleOrDefault(c => c.Id == id));

    public Task<InvoiceCase?> GetByReferenceAsync(string reference, CancellationToken ct) => Task.FromResult(Cases.SingleOrDefault(c => c.Reference == reference));

    public void Add(InvoiceCase invoiceCase) => Cases.Add(invoiceCase);

    public Task<IReadOnlyList<InvoiceFingerprint>> GetFingerprintsAsync(string? vendorNo, string vendorNameKey, string invoiceNumberKey, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<InvoiceFingerprint>>(Cases
            .Where(c => c.Status != CaseStatus.Cancelled && c.InvoiceNumber is not null
                && ((vendorNo != null && c.VendorNo == vendorNo)
                    || DuplicateDetector.NormaliseKey(c.VendorName) == vendorNameKey
                    || DuplicateDetector.NormaliseInvoiceNumber(c.InvoiceNumber) == invoiceNumberKey))
            .Select(c => new InvoiceFingerprint(c.Id, c.Reference, c.VendorNo, c.VendorName, c.InvoiceNumber!, c.Amount, c.Currency, c.InvoiceDate))
            .ToList());

    public Task<IReadOnlyList<InvoiceCase>> GetWithPendingScansAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<InvoiceCase>>(Cases.Where(c => c.Documents.Any(d => d.ScanStatus == ScanStatus.Pending)).ToList());

    public Task<IReadOnlyList<InvoiceCase>> GetAwaitingChecksAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<InvoiceCase>>(Cases.Where(c => c.Status == CaseStatus.Received && c.Channel is SubmissionChannel.Portal or SubmissionChannel.Assisted).ToList());

    public Task<IReadOnlyList<(string Reference, decimal Amount)>> GetOpenClaimsOnOrderAsync(string vendorNo, string orderNumber, Guid excludeCaseId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(string Reference, decimal Amount)>>(Cases
            .Where(c => c.Id != excludeCaseId && c.VendorNo == vendorNo && c.OrderNumber == orderNumber && c.Amount is not null
                && c.Status is CaseStatus.Received or CaseStatus.ExceptionReview or CaseStatus.WithAccountsPayable or CaseStatus.Accepted)
            .Select(c => (c.Reference, c.Amount!.Value))
            .ToList());

    private static bool WithHolder(InvoiceCase c) => c.Status is CaseStatus.WithAccountsPayable or CaseStatus.ExceptionReview;

    public Task<IReadOnlyList<InvoiceCase>> GetDueForReminderAsync(DateTimeOffset nowUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<InvoiceCase>>(Cases.Where(c => WithHolder(c) && !c.ReminderSent && c.ReminderAtUtc <= nowUtc).ToList());

    public Task<IReadOnlyList<InvoiceCase>> GetOverdueNotEscalatedAsync(DateTimeOffset nowUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<InvoiceCase>>(Cases.Where(c => WithHolder(c) && c.EscalatedAtUtc is null && c.DueAtUtc < nowUtc).ToList());

    public Task<IReadOnlyList<InvoiceCase>> GetAwaitingPaymentAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<InvoiceCase>>(Cases.Where(c => c.Status == CaseStatus.EnteredInErp).ToList());

    // IRouteRepository
    Task<CategoryRoute?> IRouteRepository.GetAsync(string code, CancellationToken ct) => Task.FromResult(Routes.SingleOrDefault(r => r.Code == code));

    public Task<IReadOnlyList<CategoryRoute>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CategoryRoute>>(Routes);

    void IRouteRepository.Add(CategoryRoute route) => Routes.Add(route);

    public List<RouteChangeRecord> RouteChanges { get; } = [];

    public void AddChange(RouteChangeRecord change) => RouteChanges.Add(change);

    // IInboundMessageLog
    public Task<bool> HasProcessedAsync(string messageId, CancellationToken ct) => Task.FromResult(Processed.ContainsKey(messageId));

    public void MarkProcessed(string messageId, string? caseReference, string outcome, DateTimeOffset atUtc) => Processed[messageId] = outcome;

    // IReferenceGenerator
    public Task<string> NextAsync(int year, CancellationToken ct) => Task.FromResult(CaseReference.Format(year, ++_seq));

    // INotificationOutbox
    public void Enqueue(OutboundNotification notification) => Sent.Add(notification);

    // IUnitOfWork
    public Task SaveChangesAsync(CancellationToken ct)
    {
        Saves++;
        foreach (var c in Cases)
        {
            c.ClearNewAuditEvents();
        }

        return Task.CompletedTask;
    }

    public void DiscardChanges()
    {
    }

    // IDocumentStore
    public async Task<StoredDocument> UploadAsync(string caseReference, int round, string fileName, string contentType, Stream content, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var path = $"{caseReference}/round-{round}/{fileName}";
        Blobs[path] = ms.ToArray();
        return new StoredDocument(path, Convert.ToHexString(SHA256.HashData(ms.ToArray())), ms.Length);
    }

    public Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Blobs[blobPath]));
}

internal sealed class FakeScanReader(ScanStatus result) : IDocumentScanReader
{
    public Task<ScanStatus> GetScanStatusAsync(string blobPath, CancellationToken ct) => Task.FromResult(result);
}

internal sealed class FakeExtractor(ExtractedInvoiceFields? result = null) : IInvoiceExtractor
{
    public Task<ExtractedInvoiceFields?> ExtractAsync(Stream pdf, CancellationToken ct) => Task.FromResult(result);
}

internal sealed class FakeErp : IErpGateway
{
    public bool IsEnabled { get; set; } = true;

    public Dictionary<string, ErpVendor> Vendors { get; } = [];

    public ErpOrderCheck Order { get; set; } = new(true, true, true, true, 10_000_000m, "NGN", ReceivedNotInvoicedAmount: 1_000_000m);

    /// <summary>When set, every call throws (Business Central unreachable).</summary>
    public bool Down { get; set; }

    public Task<ErpVendor?> GetVendorAsync(string vendorNo, CancellationToken ct) =>
        Down ? throw new HttpRequestException("Business Central unavailable") : Task.FromResult(Vendors.GetValueOrDefault(vendorNo));

    public Task<IReadOnlyList<ErpVendor>> FindVendorsByEmailAsync(string email, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ErpVendor>>(Vendors.Values.Where(v => v.Emails.Contains(email, StringComparer.OrdinalIgnoreCase)).ToList());

    public Task<ErpOrderCheck> CheckOrderAsync(string vendorNo, string orderNumber, CancellationToken ct) =>
        Down ? throw new HttpRequestException("Business Central unavailable") : Task.FromResult(Order);

    public Task<ErpPaymentStatus?> GetPaymentStatusAsync(string erpDocumentNumber, CancellationToken ct) => Task.FromResult<ErpPaymentStatus?>(null);
}

internal static class Opts
{
    public static IOptions<VimOptions> Default() => Options.Create(new VimOptions
    {
        WorkbenchUrl = "https://vim.desicon.example",
        FinanceManagerEmail = "finance.manager@desicon.example",
        AccountsPayableEmail = "ap@desicon.example",
        ManagementEscalationEmail = "dmd.office@desicon.example",
    });
}
