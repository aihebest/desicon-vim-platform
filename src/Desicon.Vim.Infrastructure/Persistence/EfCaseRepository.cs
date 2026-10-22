using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Integrity;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Vim.Infrastructure.Persistence;

/// <summary>Loads and adds whole <see cref="InvoiceCase"/> aggregates (with documents, checklist answers and flags).</summary>
public sealed class EfCaseRepository(VimDbContext db) : ICaseRepository
{
    public Task<InvoiceCase?> GetAsync(Guid id, CancellationToken ct) =>
        WithChildren().SingleOrDefaultAsync(c => c.Id == id, ct);

    public Task<InvoiceCase?> GetByReferenceAsync(string reference, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var normalised = reference.Trim().ToUpperInvariant();
        return WithChildren().SingleOrDefaultAsync(c => c.Reference == normalised, ct);
    }

    public void Add(InvoiceCase invoiceCase)
    {
        ArgumentNullException.ThrowIfNull(invoiceCase);
        db.Cases.Add(invoiceCase);
    }

    /// <summary>
    /// Candidates for duplicate detection: non-cancelled cases with an invoice number that share the
    /// vendor number, the normalised vendor name, or the normalised invoice number. The keys are
    /// already normalised by the caller and are matched against the shadow columns kept by EfUnitOfWork.
    /// The domain's DuplicateDetector then decides which candidates are real matches.
    /// </summary>
    public async Task<IReadOnlyList<InvoiceFingerprint>> GetFingerprintsAsync(string? vendorNo, string vendorNameKey, string invoiceNumberKey, CancellationToken ct)
    {
        var no = string.IsNullOrWhiteSpace(vendorNo) ? null : vendorNo.Trim();
        var nameKey = vendorNameKey ?? string.Empty;
        var numberKey = invoiceNumberKey ?? string.Empty;
        if (no is null && nameKey.Length == 0 && numberKey.Length == 0)
        {
            return [];
        }

        return await db.Cases
            .AsNoTracking()
            .Where(c => c.Status != CaseStatus.Cancelled
                && c.InvoiceNumber != null
                && ((no != null && c.VendorNo == no)
                    || (nameKey != string.Empty && EF.Property<string>(c, VimDbContext.VendorNameKeyColumn) == nameKey)
                    || (numberKey != string.Empty && EF.Property<string>(c, VimDbContext.InvoiceNumberKeyColumn) == numberKey)))
            .OrderByDescending(c => c.ReceivedAtUtc)
            .Select(c => new InvoiceFingerprint(c.Id, c.Reference, c.VendorNo, c.VendorName, c.InvoiceNumber!, c.Amount, c.Currency, c.InvoiceDate))
            .Take(500)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Open cases with at least one document still waiting for its malware scan result.</summary>
    public async Task<IReadOnlyList<InvoiceCase>> GetWithPendingScansAsync(CancellationToken ct) =>
        await WithChildren()
            .Where(c => c.Status != CaseStatus.Closed
                && c.Status != CaseStatus.Cancelled
                && c.Documents.Any(d => d.ScanStatus == ScanStatus.Pending))
            .OrderBy(c => c.ReceivedAtUtc)
            .Take(100)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<InvoiceCase>> GetAwaitingChecksAsync(CancellationToken ct) =>
        await WithChildren()
            .Where(c => c.Status == CaseStatus.Received
                && (c.Channel == SubmissionChannel.Portal || c.Channel == SubmissionChannel.Assisted))
            .OrderBy(c => c.LastChangedAtUtc)
            .Take(50)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<InvoiceCase>> GetDueForReminderAsync(DateTimeOffset nowUtc, CancellationToken ct) =>
        await WithChildren()
            .Where(c => (c.Status == CaseStatus.WithAccountsPayable || c.Status == CaseStatus.ExceptionReview)
                && !c.ReminderSent
                && c.ReminderAtUtc != null
                && c.ReminderAtUtc <= nowUtc)
            .OrderBy(c => c.ReminderAtUtc)
            .Take(200)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<InvoiceCase>> GetOverdueNotEscalatedAsync(DateTimeOffset nowUtc, CancellationToken ct) =>
        await WithChildren()
            .Where(c => (c.Status == CaseStatus.WithAccountsPayable || c.Status == CaseStatus.ExceptionReview)
                && c.EscalatedAtUtc == null
                && c.DueAtUtc != null
                && c.DueAtUtc < nowUtc)
            .OrderBy(c => c.DueAtUtc)
            .Take(200)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<InvoiceCase>> GetAwaitingPaymentAsync(CancellationToken ct) =>
        await WithChildren()
            .Where(c => c.Status == CaseStatus.EnteredInErp && c.ErpDocumentNumber != null)
            .OrderBy(c => c.LastChangedAtUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    private IQueryable<InvoiceCase> WithChildren() =>
        db.Cases
            .Include(c => c.Documents)
            .Include(c => c.ChecklistAnswers)
            .Include(c => c.Flags)
            .AsSplitQuery();
}
