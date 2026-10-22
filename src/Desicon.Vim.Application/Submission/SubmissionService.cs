using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Cases;
using Desicon.Vim.Application.Intake;
using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Domain.Cases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Application.Submission;

/// <summary>A file placed in one of the document slots.</summary>
public sealed record SubmittedFile(
    DocumentKind Kind,
    string FileName,
    string ContentType,
    long SizeBytes,
    Func<CancellationToken, Task<Stream>> OpenAsync);

/// <summary>
/// Vendor submission (portal) and assisted submission (Accounts Payable keying in for a
/// vendor who cannot use the portal). The same rules apply to both: required document
/// slots, PDF only, and the Business Central pre-check, so obvious mistakes are fixed
/// before anything is recorded. The authoritative checks run again after the malware
/// scan (<see cref="SubmissionProcessor"/>).
/// </summary>
public sealed class SubmissionService(
    ICaseRepository cases,
    IReferenceGenerator references,
    IDocumentStore documents,
    IErpGateway erp,
    AutomaticChecks checks,
    INotificationOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<VimOptions> options,
    ILogger<SubmissionService> logger)
{
    /// <summary>
    /// Everything that would stop this submission, in plain words for the vendor. Empty
    /// means it can be submitted. Business Central being unreachable is not a problem here:
    /// the checks are repeated after the scan.
    /// </summary>
    public async Task<IReadOnlyList<string>> PrecheckAsync(InvoiceSubmission submission, IReadOnlyList<SubmittedFile> files, Guid? existingCaseId, string? existingReference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(files);
        var problems = new List<string>(submission.Problems(Today()));
        await CheckFilesAsync(submission.SupplyType, files, requireAllSlots: existingCaseId is null, problems, ct).ConfigureAwait(false);

        if (problems.Count == 0)
        {
            try
            {
                var outcome = await checks.EvaluateAsync(existingCaseId ?? Guid.Empty, existingReference ?? string.Empty, submission, ct).ConfigureAwait(false);
                problems.AddRange(outcome.Rejections);
            }
            catch (ErpUnavailableException ex)
            {
                logger.LogWarning(ex, "Business Central pre-check unavailable; the checks will run after the scan.");
            }
        }

        return problems;
    }

    public async Task<InvoiceCase> SubmitAsync(Actor actor, SubmissionChannel channel, InvoiceSubmission submission, IReadOnlyList<SubmittedFile> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var problems = await PrecheckAsync(submission, files, null, null, ct).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            throw new DomainRuleException(RuleCodes.MissingDetails, string.Join(" ", problems));
        }

        var ctx = ActionContext.For(actor, clock.UtcNow);
        var reference = await references.NextAsync(ctx.NowUtc.Year, ct).ConfigureAwait(false);
        var c = InvoiceCase.Submit(reference, channel, submission, ctx);
        await AttachAsync(c, files, ctx, ct).ConfigureAwait(false);
        await FillVendorEmailAsync(c, ct).ConfigureAwait(false);

        cases.Add(c);
        if (!string.IsNullOrWhiteSpace(c.SenderEmail))
        {
            outbox.Enqueue(NotificationTemplates.Acknowledgement(c, c.SenderEmail));
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>
    /// Corrected submission against the same reference after a rejection. Only the slots
    /// being replaced need a file; the others keep the earlier documents.
    /// </summary>
    public async Task ResubmitAsync(Guid caseId, Actor actor, InvoiceSubmission corrected, IReadOnlyList<SubmittedFile> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(files);
        var c = await cases.GetAsync(caseId, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Case {caseId} was not found.");
        if (files.Count == 0)
        {
            throw new DomainRuleException(RuleCodes.MissingDocuments, "Attach at least one corrected document.");
        }

        var problems = await PrecheckAsync(corrected, files, c.Id, c.Reference, ct).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            throw new DomainRuleException(RuleCodes.MissingDetails, string.Join(" ", problems));
        }

        var ctx = ActionContext.For(actor, clock.UtcNow);
        c.Resubmit(corrected, ctx);
        await AttachAsync(c, files, ctx, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(c.SenderEmail))
        {
            outbox.Enqueue(NotificationTemplates.Acknowledgement(c, c.SenderEmail));
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task CheckFilesAsync(SupplyType supply, IReadOnlyList<SubmittedFile> files, bool requireAllSlots, List<string> problems, CancellationToken ct)
    {
        if (requireAllSlots && supply != SupplyType.Unknown)
        {
            foreach (var kind in RequiredDocuments.For(supply).Where(k => files.All(f => f.Kind != k)))
            {
                problems.Add($"{RequiredDocuments.Label(kind)} is required.");
            }
        }

        var maxBytes = options.Value.MaxAttachmentMegabytes * 1024L * 1024L;
        foreach (var f in files)
        {
            if (f.SizeBytes <= 0 || f.SizeBytes > maxBytes)
            {
                problems.Add($"{f.FileName}: files must be between 1 byte and {options.Value.MaxAttachmentMegabytes} MB.");
            }
            else if (!await PdfSignature.IsPdfAsync(f.OpenAsync, ct).ConfigureAwait(false))
            {
                problems.Add($"{f.FileName} is not a valid PDF. Scan documents to PDF (not photos).");
            }
        }
    }

    private async Task AttachAsync(InvoiceCase c, IReadOnlyList<SubmittedFile> files, ActionContext ctx, CancellationToken ct)
    {
        foreach (var f in files)
        {
            var stream = await f.OpenAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var stored = await documents.UploadAsync(c.Reference, c.SubmissionRound, f.FileName, "application/pdf", stream, ct).ConfigureAwait(false);
                c.AttachDocument(f.Kind, f.FileName, "application/pdf", stored.SizeBytes, stored.Sha256, stored.BlobPath, ctx);
            }
        }
    }

    /// <summary>No email given: use the one on the Business Central vendor card for notices.</summary>
    private async Task FillVendorEmailAsync(InvoiceCase c, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(c.SenderEmail) || !erp.IsEnabled || c.VendorNo is null)
        {
            return;
        }

        try
        {
            var vendor = await erp.GetVendorAsync(c.VendorNo, ct).ConfigureAwait(false);
            c.SetVendorEmailIfMissing(vendor is { Emails.Count: > 0 } ? vendor.Emails[0] : null);
        }
#pragma warning disable CA1031 // Best effort only.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            logger.LogWarning(ex, "Could not read the vendor email for {Reference} from Business Central.", c.Reference);
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.UtcNow.ToOffset(TimeSpan.FromHours(1)).DateTime);
}
