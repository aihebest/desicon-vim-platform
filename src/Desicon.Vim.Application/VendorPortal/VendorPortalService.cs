using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Cases;
using Desicon.Vim.Application.Submission;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Integrity;
using Desicon.Vim.Domain.VendorAccess;

namespace Desicon.Vim.Application.VendorPortal;

/// <summary>What the vendor types on the portal. The vendor itself always comes from the login.</summary>
public sealed record VendorInvoiceForm(
    string InvoiceNumber,
    DateOnly InvoiceDate,
    decimal Amount,
    string Currency,
    string OrderNumber,
    SupplyType SupplyType,
    DateOnly DeliveredOn);

/// <summary>One row of the vendor's invoice list.</summary>
public sealed record VendorInvoiceSummary(
    Guid Id,
    string Reference,
    CaseStatus Status,
    string StatusText,
    bool NeedsAction,
    string? InvoiceNumber,
    decimal? Amount,
    string? Currency,
    string? OrderNumber,
    DateTimeOffset SubmittedAtUtc,
    DateTimeOffset? AcceptedAtUtc);

public sealed record VendorDocumentView(string Label, string FileName, int Round, DateTimeOffset AddedAtUtc, string ScanText);

/// <summary>The vendor's view of one invoice: no internal notes, reviewers or checklist answers.</summary>
public sealed record VendorInvoiceDetail(
    VendorInvoiceSummary Summary,
    DateOnly? InvoiceDate,
    DateOnly? DeliveredOn,
    SupplyType SupplyType,
    int Round,
    string? ReturnReason,
    IReadOnlyList<VendorDocumentView> Documents,
    bool CanResubmit);

/// <summary>Either the problems to fix, or the case that was created/updated.</summary>
public sealed record VendorSubmitResult(IReadOnlyList<string> Problems, Guid? CaseId, string? Reference)
{
    public bool Succeeded => Problems.Count == 0 && CaseId is not null;
}

/// <summary>
/// Vendor portal phase 2: a linked vendor user submits invoices with the agreed document
/// slots, follows their status and corrects returned ones. Every call is scoped to the
/// login's own vendor: the vendor number never comes from the browser, and another vendor's
/// case behaves exactly like a case that does not exist.
/// </summary>
public sealed class VendorPortalService(ICaseRepository cases, SubmissionService submissions, IClock clock)
{
    public const int ListSize = 200;

    public async Task<IReadOnlyList<VendorInvoiceSummary>> ListAsync(VendorUser user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var list = await cases.ListForVendorAsync(user.VendorNo, ListSize, ct).ConfigureAwait(false);
        return list.Where(c => IsOwn(c, user)).Select(Summarise).ToList();
    }

    /// <summary>Null when the case does not exist or belongs to another vendor.</summary>
    public async Task<VendorInvoiceDetail?> GetAsync(VendorUser user, Guid caseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var c = await cases.GetAsync(caseId, ct).ConfigureAwait(false);
        if (c is null || !IsOwn(c, user))
        {
            return null;
        }

        var docs = c.CurrentDocuments
            .OrderBy(d => d.Kind)
            .Select(d => new VendorDocumentView(RequiredDocuments.Label(d.Kind), d.FileName, d.SubmissionRound, d.AddedAtUtc, ScanText(d.ScanStatus)))
            .ToList();
        return new VendorInvoiceDetail(
            Summarise(c),
            c.InvoiceDate,
            c.DeliveredOn,
            c.SupplyType,
            c.SubmissionRound,
            c.Status == CaseStatus.ReturnedToVendor ? c.LastReason : null,
            docs,
            CanResubmitOnPortal(c));
    }

    public async Task<VendorSubmitResult> SubmitAsync(VendorUser user, VendorInvoiceForm form, IReadOnlyList<SubmittedFile> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(files);
        var submission = ToSubmission(user, form);

        // A double-click or a browser retry of a slow upload must not create a second case
        // (the two would then block each other as duplicates): the same invoice number from
        // this vendor on the portal in the last few minutes is treated as the same submission.
        var number = DuplicateDetector.NormaliseInvoiceNumber(submission.InvoiceNumber);
        var recent = (await cases.ListForVendorAsync(user.VendorNo, 20, ct).ConfigureAwait(false))
            .FirstOrDefault(c => IsOwn(c, user)
                && c.Channel == SubmissionChannel.Portal
                && c.Status != CaseStatus.Cancelled
                && c.ReceivedAtUtc >= clock.UtcNow - RetryWindow
                && number.Length > 0
                && DuplicateDetector.NormaliseInvoiceNumber(c.InvoiceNumber) == number);
        if (recent is not null)
        {
            return new VendorSubmitResult([], recent.Id, recent.Reference);
        }

        var problems = await submissions.PrecheckAsync(submission, files, null, null, ct).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            return new VendorSubmitResult(problems, null, null);
        }

        try
        {
            var c = await submissions.SubmitAsync(user.ToActor(), SubmissionChannel.Portal, submission, files, ct).ConfigureAwait(false);
            return new VendorSubmitResult([], c.Id, c.Reference);
        }
        catch (DomainRuleException ex)
        {
            return new VendorSubmitResult([ex.Message], null, null);
        }
    }

    /// <summary>A repeat of the same invoice number within this window is a retry, not a new invoice.</summary>
    public static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(10);

    /// <summary>Corrected details and replacement documents for a returned invoice.</summary>
    public async Task<VendorSubmitResult> ResubmitAsync(VendorUser user, Guid caseId, VendorInvoiceForm form, IReadOnlyList<SubmittedFile> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(files);
        var c = await cases.GetAsync(caseId, ct).ConfigureAwait(false);
        if (c is null || !IsOwn(c, user))
        {
            throw new NotFoundException("Invoice not found.");
        }

        if (!CanResubmitOnPortal(c))
        {
            return new VendorSubmitResult([c.Status == CaseStatus.ReturnedToVendor
                ? "This invoice was not submitted on the portal. Please send the correction to Desicon Accounts Payable."
                : "This invoice is not waiting for a correction."], null, null);
        }

        var submission = ToSubmission(user, form);
        var problems = files.Count == 0
            ? ["Attach at least one corrected document."]
            : await submissions.PrecheckAsync(submission, files, c.Id, c.Reference, ct).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            return new VendorSubmitResult(problems, null, null);
        }

        try
        {
            await submissions.ResubmitAsync(c.Id, user.ToActor(), submission, files, ct).ConfigureAwait(false);
            return new VendorSubmitResult([], c.Id, c.Reference);
        }
        catch (DomainRuleException ex)
        {
            return new VendorSubmitResult([ex.Message], null, null);
        }
        catch (ConcurrencyConflictException)
        {
            return new VendorSubmitResult(["Desicon updated this invoice while you were correcting it. Reload the page to see its latest status."], null, null);
        }
    }

    /// <summary>Plain-words status for the vendor.</summary>
    public static string StatusText(CaseStatus status) => status switch
    {
        CaseStatus.Received => "Received: automatic checks in progress",
        CaseStatus.WithAccountsPayable => "With Desicon Accounts for acceptance",
        CaseStatus.ExceptionReview => "Being reviewed by Desicon Finance",
        CaseStatus.ReturnedToVendor => "Returned to you: correction needed",
        CaseStatus.Accepted => "Accepted",
        CaseStatus.EnteredInErp => "Accepted and entered for payment",
        CaseStatus.Paid => "Paid",
        CaseStatus.Closed => "Closed",
        CaseStatus.Cancelled => "Cancelled",
        _ => "In progress",
    };

    private static InvoiceSubmission ToSubmission(VendorUser user, VendorInvoiceForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new InvoiceSubmission(
            user.VendorNo,
            user.VendorName,
            user.Email,
            form.InvoiceNumber?.Trim() ?? string.Empty,
            form.InvoiceDate,
            form.Amount,
            (form.Currency ?? string.Empty).Trim().ToUpperInvariant(),
            form.OrderNumber?.Trim() ?? string.Empty,
            form.SupplyType,
            form.DeliveredOn);
    }

    /// <summary>
    /// Only portal or assisted submissions carry the document slots and supply type the
    /// portal form needs; returned email/paper invoices are corrected through Accounts Payable.
    /// </summary>
    private static bool CanResubmitOnPortal(InvoiceCase c) =>
        c.Status == CaseStatus.ReturnedToVendor
        && c.Channel is SubmissionChannel.Portal or SubmissionChannel.Assisted
        && c.SupplyType is SupplyType.Materials or SupplyType.Services;

    private static bool IsOwn(InvoiceCase c, VendorUser user) =>
        c.VendorNo is not null && string.Equals(c.VendorNo, user.VendorNo, StringComparison.OrdinalIgnoreCase);

    private static VendorInvoiceSummary Summarise(InvoiceCase c) => new(
        c.Id,
        c.Reference,
        c.Status,
        StatusText(c.Status),
        c.Status == CaseStatus.ReturnedToVendor,
        c.InvoiceNumber,
        c.Amount,
        c.Currency,
        c.OrderNumber,
        c.ReceivedAtUtc,
        c.AcceptedAtUtc);

    private static string ScanText(ScanStatus s) => s switch
    {
        ScanStatus.Pending => "Virus check in progress",
        ScanStatus.Clean => "Checked",
        ScanStatus.ScanFailed => "Being checked again",
        _ => "Rejected by the virus check",
    };
}
