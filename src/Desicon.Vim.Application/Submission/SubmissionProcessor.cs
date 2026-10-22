using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Domain.Cases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Application.Submission;

public sealed record ProcessorRunResult(int ToAccounts, int ToFinanceManager, int Returned, int Waiting);

/// <summary>
/// Runs every minute after the malware-scan sync. For each submission whose documents
/// have all been scanned it runs the automatic checks and moves it on:
/// rejected with reasons → vendor; needs a decision → Finance Manager; clean → Accounts.
/// Each case is saved on its own, so one failure never holds up the others.
/// </summary>
public sealed class SubmissionProcessor(
    ICaseRepository cases,
    AutomaticChecks checks,
    INotificationOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<VimOptions> options,
    ILogger<SubmissionProcessor> logger)
{
    public const string AccountsHolder = "Accounts Payable";
    public const string FinanceManagerHolder = "Finance Manager";

    public async Task<ProcessorRunResult> RunAsync(CancellationToken ct)
    {
        int toAccounts = 0, toFm = 0, returned = 0, waiting = 0;
        foreach (var c in await cases.GetAwaitingChecksAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var moved = await ProcessAsync(c, ct).ConfigureAwait(false);
                switch (moved)
                {
                    case CaseStatus.WithAccountsPayable: toAccounts++; break;
                    case CaseStatus.ExceptionReview: toFm++; break;
                    case CaseStatus.ReturnedToVendor: returned++; break;
                    default: waiting++; break;
                }
            }
#pragma warning disable CA1031 // One bad case must not stop the run; it is retried next minute.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                // Forget this case's half-made changes so the next case's save does not carry them.
                unitOfWork.DiscardChanges();
                logger.LogError(ex, "Automatic checks failed for {Reference}; will retry.", c.Reference);
                waiting++;
            }
        }

        return new ProcessorRunResult(toAccounts, toFm, returned, waiting);
    }

    /// <summary>Returns the new status, or Received if the case must wait (scan pending, BC unreachable).</summary>
    public async Task<CaseStatus> ProcessAsync(InvoiceCase c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (!c.AllCurrentDocumentsScanned)
        {
            return CaseStatus.Received;
        }

        var now = clock.UtcNow;
        var ctx = ActionContext.For(Actor.System, now);
        var o = options.Value;

        var infected = c.CurrentDocuments.Where(d => d.ScanStatus == ScanStatus.Malicious).ToList();
        if (infected.Count > 0)
        {
            return await ReturnAsync(c,
                $"These files failed the virus scan: {string.Join(", ", infected.Select(d => d.FileName))}. Please scan the documents again to clean PDFs and resubmit.",
                ctx, ct).ConfigureAwait(false);
        }

        var unscannable = c.CurrentDocuments.Where(d => d.ScanStatus == ScanStatus.ScanFailed).ToList();
        if (unscannable.Count > 0)
        {
            return await ReturnAsync(c,
                $"These files could not be read by our security scan (for example password-protected or damaged): {string.Join(", ", unscannable.Select(d => d.FileName))}. Please save them again as ordinary PDFs and resubmit.",
                ctx, ct).ConfigureAwait(false);
        }

        var missing = c.MissingRequiredDocuments;
        if (missing.Count > 0)
        {
            return await ReturnAsync(c, "Missing documents: " + string.Join(", ", missing.Select(RequiredDocuments.Label)) + ".", ctx, ct).ConfigureAwait(false);
        }

        CheckOutcome outcome;
        try
        {
            outcome = await checks.EvaluateAsync(c.Id, c.Reference, ToSubmission(c), ct).ConfigureAwait(false);
        }
        catch (ErpUnavailableException ex)
        {
            if (now - c.LastChangedAtUtc < TimeSpan.FromHours(o.ErpUnavailableEscalationHours))
            {
                logger.LogWarning(ex, "Business Central unavailable for {Reference}; will retry.", c.Reference);
                return CaseStatus.Received;
            }

            outcome = new CheckOutcome([], [new CheckFlag(FlagCodes.ErpUnavailable,
                $"Business Central could not be reached for {o.ErpUnavailableEscalationHours:0.#} hours, so the PO, receipt and amount checks have not run. Check them in Business Central before releasing.",
                Blocking: true)]);
        }

        if (outcome.Rejected)
        {
            return await ReturnAsync(c, string.Join(" ", outcome.Rejections), ctx, ct).ConfigureAwait(false);
        }

        foreach (var f in outcome.Flags)
        {
            c.RaiseFlag(f.Code, f.Description, f.Blocking, ctx);
        }

        if (c.HasOpenBlockingFlags)
        {
            c.RaiseException(o.Deadlines.Create(FinanceManagerHolder, now, o.ExceptionReviewWorkingDays), ctx);
            if (!string.IsNullOrWhiteSpace(o.FinanceManagerEmail))
            {
                outbox.Enqueue(NotificationTemplates.ExceptionForFinanceManager(c, o.FinanceManagerEmail, o.WorkbenchUrl));
            }
        }
        else
        {
            c.PassToAccounts(o.Deadlines.Create(AccountsHolder, now, o.AccountsCheckWorkingDays), ctx);
            if (!string.IsNullOrWhiteSpace(o.AccountsPayableEmail))
            {
                outbox.Enqueue(NotificationTemplates.ReadyForAccounts(c, o.AccountsPayableEmail, o.WorkbenchUrl));
            }
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return c.Status;
    }

    private async Task<CaseStatus> ReturnAsync(InvoiceCase c, string reason, ActionContext ctx, CancellationToken ct)
    {
        c.ReturnToVendor(reason, ctx);
        if (!string.IsNullOrWhiteSpace(c.SenderEmail))
        {
            outbox.Enqueue(NotificationTemplates.ReturnedToVendor(c, c.SenderEmail, reason));
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return c.Status;
    }

    internal static InvoiceSubmission ToSubmission(InvoiceCase c) => new(
        c.VendorNo ?? string.Empty,
        c.VendorName ?? string.Empty,
        c.SenderEmail,
        c.InvoiceNumber ?? string.Empty,
        c.InvoiceDate ?? default,
        c.Amount ?? 0,
        c.Currency ?? string.Empty,
        c.OrderNumber ?? string.Empty,
        c.SupplyType,
        c.DeliveredOn ?? default);
}
