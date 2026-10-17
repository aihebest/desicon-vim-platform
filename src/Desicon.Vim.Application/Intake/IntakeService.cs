using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Integrity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Application.Intake;

public sealed record InboundAttachment(string FileName, string ContentType, long SizeBytes, Func<CancellationToken, Task<Stream>> OpenAsync);

/// <summary>An email read from the invoice mailbox by the intake function.</summary>
/// <param name="SenderAuthenticated">
/// True when SPF/DKIM/DMARC passed (from the Authentication-Results header).
/// False marks a possible spoof; null means the header was missing.
/// </param>
public sealed record InboundEmail(
    string MessageId,
    string FromAddress,
    string? FromName,
    string? Subject,
    DateTimeOffset ReceivedAtUtc,
    bool? SenderAuthenticated,
    IReadOnlyList<InboundAttachment> Attachments);

public enum IntakeOutcome
{
    AlreadyProcessed,
    CreatedCase,
    AddedToResubmission,
    RejectedNoPdf,
}

public sealed record IntakeResult(IntakeOutcome Outcome, string? CaseReference);

/// <summary>
/// Turns an inbound email into a case. Safe to run twice on the same email:
/// the message id is recorded and a repeat is ignored.
/// </summary>
public sealed class IntakeService(
    ICaseRepository cases,
    IInboundMessageLog messageLog,
    IReferenceGenerator references,
    IDocumentStore documents,
    IInvoiceExtractor extractor,
    IErpGateway erp,
    INotificationOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<VimOptions> options,
    ILogger<IntakeService> logger)
{
    private const string PdfContentType = "application/pdf";

    public async Task<IntakeResult> ProcessEmailAsync(InboundEmail email, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);

        if (await messageLog.HasProcessedAsync(email.MessageId, ct).ConfigureAwait(false))
        {
            return new IntakeResult(IntakeOutcome.AlreadyProcessed, null);
        }

        var now = clock.UtcNow;
        var ctx = new ActionContext(Actor.System, now, email.MessageId);
        var maxBytes = options.Value.MaxAttachmentMegabytes * 1024L * 1024L;

        var pdfs = new List<InboundAttachment>();
        foreach (var a in email.Attachments.Where(a => IsPdf(a) && a.SizeBytes > 0 && a.SizeBytes <= maxBytes))
        {
            // Trust the file's content, not its name: a real PDF starts with "%PDF-".
            if (await PdfSignature.IsPdfAsync(a.OpenAsync, ct).ConfigureAwait(false))
            {
                pdfs.Add(a);
            }
        }

        if (pdfs.Count == 0)
        {
            outbox.Enqueue(NotificationTemplates.NonPdfRejected(email.FromAddress, email.Subject));
            messageLog.MarkProcessed(email.MessageId, null, nameof(IntakeOutcome.RejectedNoPdf), now);
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Rejected email {MessageId} from {From}: no acceptable PDF.", email.MessageId, email.FromAddress);
            return new IntakeResult(IntakeOutcome.RejectedNoPdf, null);
        }

        // A reply quoting an existing reference is a resubmission of that case.
        InvoiceCase? invoiceCase = null;
        var quotedReference = CaseReference.FindIn(email.Subject);
        if (quotedReference is not null)
        {
            var existing = await cases.GetByReferenceAsync(quotedReference, ct).ConfigureAwait(false);
            // References are sequential and guessable, so only the original sender or a
            // known Business Central vendor contact may add documents to a returned case.
            if (existing is { Status: CaseStatus.ReturnedToVendor }
                && await IsTrustedResubmitterAsync(existing, email.FromAddress, ct).ConfigureAwait(false))
            {
                existing.Resubmit(email.Subject, ctx);
                invoiceCase = existing;
            }
        }

        var isResubmission = invoiceCase is not null;
        if (invoiceCase is null)
        {
            var reference = await references.NextAsync(now.Year, ct).ConfigureAwait(false);
            invoiceCase = InvoiceCase.Receive(reference, SubmissionChannel.Email, email.FromAddress, email.FromName, email.Subject, ctx);
            cases.Add(invoiceCase);
        }

        // Store every acceptable PDF; extract fields from the first one.
        ExtractedInvoiceFields? extracted = null;
        foreach (var attachment in pdfs)
        {
            await using var stream = await attachment.OpenAsync(ct).ConfigureAwait(false);
            var stored = await documents.UploadAsync(invoiceCase.Reference, invoiceCase.SubmissionRound, attachment.FileName, PdfContentType, stream, ct).ConfigureAwait(false);
            var kind = pdfs.Count == 1 ? DocumentKind.SubmissionPackage : DocumentKind.Other;
            invoiceCase.AttachDocument(kind, attachment.FileName, PdfContentType, stored.SizeBytes, stored.Sha256, stored.BlobPath, ctx);

            if (extracted is null)
            {
                await using var forExtraction = await documents.OpenReadAsync(stored.BlobPath, ct).ConfigureAwait(false);
                extracted = await extractor.ExtractAsync(forExtraction, ct).ConfigureAwait(false);
            }
        }

        if (extracted is not null)
        {
            invoiceCase.RecordExtraction(extracted, ctx);
        }

        var ignored = email.Attachments.Count - pdfs.Count;
        if (ignored > 0)
        {
            logger.LogInformation("Ignored {Count} non-PDF or oversized attachment(s) on {Reference}.", ignored, invoiceCase.Reference);
        }

        await RaiseSenderFlagsAsync(invoiceCase, email, ctx, ct).ConfigureAwait(false);

        outbox.Enqueue(NotificationTemplates.Acknowledgement(invoiceCase, email.FromAddress));
        var outcome = isResubmission ? IntakeOutcome.AddedToResubmission : IntakeOutcome.CreatedCase;
        messageLog.MarkProcessed(email.MessageId, invoiceCase.Reference, outcome.ToString(), now);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Email {MessageId} → {Reference} ({Outcome}).", email.MessageId, invoiceCase.Reference, outcome);
        return new IntakeResult(outcome, invoiceCase.Reference);
    }

    private async Task<bool> IsTrustedResubmitterAsync(InvoiceCase existing, string from, CancellationToken ct)
    {
        if (string.Equals(existing.SenderEmail, from.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!erp.IsEnabled || string.IsNullOrWhiteSpace(existing.VendorNo))
        {
            return false;
        }

        var vendor = await erp.GetVendorAsync(existing.VendorNo, ct).ConfigureAwait(false);
        return vendor?.Emails.Contains(from.Trim(), StringComparer.OrdinalIgnoreCase) == true;
    }

    private async Task RaiseSenderFlagsAsync(InvoiceCase invoiceCase, InboundEmail email, ActionContext ctx, CancellationToken ct)
    {
        if (email.SenderAuthenticated == false)
        {
            invoiceCase.RaiseFlag(FlagCodes.SenderAuthenticationFailed,
                "The sender's domain failed email authentication (SPF/DKIM/DMARC). Possible spoofing: verify with the vendor by phone through a known contact.",
                blocking: true, ctx);
        }
        else if (email.SenderAuthenticated is null)
        {
            invoiceCase.RaiseFlag(FlagCodes.SenderAuthenticationUnknown,
                "No email authentication result was available for the sender. Treat bank-detail or payment instructions with extra care.",
                blocking: false, ctx);
        }

        if (erp.IsEnabled)
        {
            var known = await erp.FindVendorsByEmailAsync(email.FromAddress, ct).ConfigureAwait(false);
            if (known.Count == 0)
            {
                invoiceCase.RaiseFlag(FlagCodes.UnknownSender,
                    "The sender's email address is not on any Business Central vendor record.",
                    blocking: false, ctx);
            }
        }
    }

    private static bool IsPdf(InboundAttachment a) =>
        string.Equals(a.ContentType, PdfContentType, StringComparison.OrdinalIgnoreCase)
        || a.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
}
