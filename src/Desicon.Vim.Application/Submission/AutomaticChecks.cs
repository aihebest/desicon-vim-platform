using System.Globalization;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Integrity;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Application.Submission;

/// <summary>Business Central could not be reached (or refused the request); the checks must wait.</summary>
public sealed class ErpUnavailableException : Exception
{
    public ErpUnavailableException()
        : base("Business Central is unavailable.")
    {
    }

    public ErpUnavailableException(string message)
        : base(message)
    {
    }

    public ErpUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A flag an automatic check wants on the case.</summary>
public sealed record CheckFlag(string Code, string Description, bool Blocking);

/// <summary>
/// What the automatic checks found. Rejections go straight back to the vendor with the
/// reasons; blocking flags go to the Finance Manager; otherwise the case goes to Accounts.
/// </summary>
public sealed record CheckOutcome(IReadOnlyList<string> Rejections, IReadOnlyList<CheckFlag> Flags)
{
    public bool Rejected => Rejections.Count > 0;

    public bool NeedsFinanceManager => !Rejected && Flags.Any(f => f.Blocking);
}

/// <summary>
/// The checks agreed at the stakeholder meeting, run against Business Central (read-only)
/// and the case register: vendor active, PO / SO belongs to the vendor and is open, a
/// receipt (GRV / TRV) is posted, the invoice is for the full received value, currency,
/// and duplicates. Used twice: as a pre-check on the submission form (so the vendor can
/// fix mistakes at once) and authoritatively once the documents have been scanned.
/// </summary>
public sealed class AutomaticChecks(ICaseRepository cases, IErpGateway erp, IOptions<VimOptions> options)
{
    public async Task<CheckOutcome> EvaluateAsync(
        Guid caseId,
        string reference,
        InvoiceSubmission s,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var rejections = new List<string>();
        var flags = new List<CheckFlag>();

        if (erp.IsEnabled)
        {
            try
            {
                await CheckErpAsync(caseId, s, rejections, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ErpUnavailableException)
            {
                throw new ErpUnavailableException("Business Central could not be read: " + ex.Message, ex);
            }
        }
        else
        {
            // Fail closed: without Business Central nothing has checked the PO, receipt or amount.
            flags.Add(new CheckFlag(FlagCodes.ErpUnavailable,
                "Business Central checks are switched off, so the PO / SO, posted receipt and full-value checks have not run. Check them in Business Central before releasing.",
                Blocking: true));
        }

        await CheckDuplicatesAsync(caseId, reference, s, rejections, flags, ct).ConfigureAwait(false);
        return new CheckOutcome(rejections, flags);
    }

    private async Task CheckErpAsync(Guid caseId, InvoiceSubmission s, List<string> rejections, CancellationToken ct)
    {
        var vendorNo = s.VendorNo.Trim().ToUpperInvariant();
        var orderNo = s.OrderNumber.Trim().ToUpperInvariant();
        var currency = s.Currency.Trim().ToUpperInvariant();

        var vendor = await erp.GetVendorAsync(vendorNo, ct).ConfigureAwait(false);
        if (vendor is null)
        {
            rejections.Add($"Vendor number {vendorNo} is not registered with Desicon. Please contact Procurement.");
            return;
        }

        if (vendor.Blocked)
        {
            rejections.Add($"Vendor account {vendorNo} is on hold. Please contact Procurement before invoicing.");
            return;
        }

        var order = await erp.CheckOrderAsync(vendorNo, orderNo, ct).ConfigureAwait(false);
        if (!order.OrderFound)
        {
            rejections.Add($"PO / SO {orderNo} was not found, or it has already been fully invoiced.");
            return;
        }

        if (!order.BelongsToVendor)
        {
            rejections.Add($"PO / SO {orderNo} is not issued to vendor {vendorNo}.");
            return;
        }

        if (!order.IsOpen)
        {
            rejections.Add($"PO / SO {orderNo} is not released for invoicing.");
        }

        if (order.Currency is not null && !string.Equals(order.Currency, currency, StringComparison.OrdinalIgnoreCase))
        {
            rejections.Add($"The invoice currency ({currency}) differs from the currency of {orderNo} ({order.Currency}).");
        }

        var received = order.ReceivedNotInvoicedAmount;
        if (!order.HasPostedReceipt || received is null or <= 0)
        {
            rejections.Add($"No delivery has been confirmed against {orderNo} yet (no GRV / TRV posted, or everything received is already invoiced). " +
                           "The department must confirm delivery before the invoice can be submitted.");
            return;
        }

        // Other VIM cases on this order that BC does not show as invoiced yet claim part of the received value.
        var claims = await cases.GetOpenClaimsOnOrderAsync(vendorNo, orderNo, caseId, ct).ConfigureAwait(false);
        var available = received.Value - claims.Sum(c => c.Amount);
        var tolerance = options.Value.FullValueTolerance;
        if (claims.Count > 0 && available <= tolerance)
        {
            rejections.Add($"The value received on {orderNo} is already being invoiced under {string.Join(", ", claims.Select(c => c.Reference))}. " +
                           "Wait for the department to confirm the next delivery before invoicing again.");
            return;
        }

        if (Math.Abs(s.Amount - available) > tolerance)
        {
            rejections.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The invoice amount ({currency} {s.Amount:N2}) does not equal the value received and not yet invoiced on {orderNo} ({currency} {available:N2}, VAT included). Desicon accepts invoices for the full received value only."));
        }
    }

    private async Task CheckDuplicatesAsync(Guid caseId, string reference, InvoiceSubmission s, List<string> rejections, List<CheckFlag> flags, CancellationToken ct)
    {
        var existing = await cases.GetFingerprintsAsync(
            s.VendorNo.Trim().ToUpperInvariant(),
            DuplicateDetector.NormaliseKey(s.VendorName),
            DuplicateDetector.NormaliseInvoiceNumber(s.InvoiceNumber),
            ct).ConfigureAwait(false);
        var candidate = new InvoiceFingerprint(caseId, reference, s.VendorNo.Trim().ToUpperInvariant(), s.VendorName, s.InvoiceNumber, s.Amount, s.Currency.Trim().ToUpperInvariant(), s.InvoiceDate);
        var matches = DuplicateDetector.FindMatches(candidate, existing);

        var sameNumber = matches.Where(m => m.Kind == DuplicateKind.SameInvoiceNumber).ToList();
        if (sameNumber.Count > 0)
        {
            rejections.Add($"Invoice {s.InvoiceNumber.Trim()} has already been submitted under {string.Join(", ", sameNumber.Select(m => m.ExistingReference))}. " +
                           "To correct a returned invoice, resubmit it against that reference.");
        }

        var others = matches.Except(sameNumber).ToList();
        if (others.Count > 0)
        {
            flags.Add(new CheckFlag(FlagCodes.PossibleDuplicate,
                "Possible duplicate of: " + string.Join("; ", others.Select(m => $"{m.ExistingReference} ({m.Kind})")) + ".",
                Blocking: true));
        }
    }
}
