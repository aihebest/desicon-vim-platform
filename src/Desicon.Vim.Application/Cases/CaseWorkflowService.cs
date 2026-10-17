using System.Globalization;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Checklists;
using Desicon.Vim.Domain.Integrity;
using Desicon.Vim.Domain.Workflow;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Application.Cases;

/// <summary>Thrown when a case or route does not exist.</summary>
public sealed class NotFoundException(string message) : Exception(message)
{
    public NotFoundException()
        : this("Not found.")
    {
    }

    public NotFoundException(string message, Exception inner)
        : this(message + " " + inner.Message)
    {
    }
}

/// <summary>
/// The people-driven steps of the workflow. Each method loads the case,
/// applies one domain action, runs the automated checks that belong to that
/// step, queues notifications and saves everything in one transaction.
/// </summary>
public sealed class CaseWorkflowService(
    ICaseRepository cases,
    IRouteRepository routes,
    IReferenceGenerator references,
    IDocumentStore documents,
    IErpGateway erp,
    INotificationOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<VimOptions> options)
{
    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private readonly ChecklistDefinition _checklist = DesiconInvoiceChecklist.Version1;

    public ChecklistDefinition Checklist => _checklist;

    /// <summary>A file the Document Controller scanned from a physical submission.</summary>
    public sealed record ScannedFile(string FileName, string ContentType, Func<CancellationToken, Task<Stream>> OpenAsync);

    /// <summary>Physical submission scanned in by the Document Controller (SOP keeps paper submission alongside email).</summary>
    public async Task<InvoiceCase> ReceivePhysicalAsync(Actor actor, string? vendorEmail, string vendorName, IReadOnlyList<ScannedFile> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new DomainRuleException(RuleCodes.MissingDetails, "Attach at least one scanned PDF.");
        }

        var ctx = Ctx(actor);
        var reference = await references.NextAsync(ctx.NowUtc.Year, ct).ConfigureAwait(false);
        var c = InvoiceCase.Receive(reference, SubmissionChannel.Physical, vendorEmail, vendorName, "Physical submission", ctx);
        foreach (var f in files)
        {
            if (!await Intake.PdfSignature.IsPdfAsync(f.OpenAsync, ct).ConfigureAwait(false))
            {
                throw new DomainRuleException(RuleCodes.InvalidValue, $"{f.FileName} is not a valid PDF.");
            }
        }

        foreach (var f in files)
        {
            await using var stream = await f.OpenAsync(ct).ConfigureAwait(false);
            var stored = await documents.UploadAsync(reference, c.SubmissionRound, f.FileName, f.ContentType, stream, ct).ConfigureAwait(false);
            c.AttachDocument(DocumentKind.SubmissionPackage, f.FileName, f.ContentType, stored.SizeBytes, stored.Sha256, stored.BlobPath, ctx);
        }

        cases.Add(c);
        if (!string.IsNullOrWhiteSpace(vendorEmail))
        {
            outbox.Enqueue(NotificationTemplates.Acknowledgement(c, vendorEmail));
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return c;
    }

    public async Task RegisterAsync(Guid caseId, InvoiceDetails details, IReadOnlyCollection<ChecklistResponse> answers, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(details);
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        var ctx = Ctx(actor);

        if (await routes.GetAsync(details.CategoryCode, ct).ConfigureAwait(false) is not { Active: true })
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, $"'{details.CategoryCode}' is not an active invoice category.");
        }

        // With ERP validation on, a missing vendor number would silently skip every ERP check.
        if (erp.IsEnabled && string.IsNullOrWhiteSpace(details.VendorNo))
        {
            throw new DomainRuleException(RuleCodes.MissingDetails, "The Business Central vendor number is required.");
        }

        c.Register(details, answers, _checklist, ctx);

        await CheckDuplicatesAsync(c, ctx, ct).ConfigureAwait(false);
        await CheckAgainstErpAsync(c, ctx, ct).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SendForVerificationAsync(Guid caseId, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        var ctx = Ctx(actor);
        var assignment = await BuildAssignmentAsync(c.CategoryCode, ctx.NowUtc, ct).ConfigureAwait(false);
        c.SendForVerification(assignment, ctx);
        NotifyReviewer(c, assignment);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>People a case in this category may be reassigned to: the route's reviewer and delegates only.</summary>
    public async Task<IReadOnlyList<(string Id, string Name)>> ReassignCandidatesAsync(Guid caseId, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        var route = c.CategoryCode is null ? null : await routes.GetAsync(c.CategoryCode, ct).ConfigureAwait(false);
        if (route is null)
        {
            return [];
        }

        return [(route.ReviewerId, route.ReviewerName), .. route.DelegateIdList.Select(d => (d, $"Delegate for {route.ReviewerName} ({d})"))];
    }

    /// <summary>
    /// Moves the case to another person on the same route. A reviewer outside the
    /// route requires the Finance Manager to change the route first (which is audited).
    /// </summary>
    public async Task ReassignAsync(Guid caseId, string reviewerId, string reason, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        var ctx = Ctx(actor);
        var candidates = await ReassignCandidatesAsync(caseId, ct).ConfigureAwait(false);
        var chosen = candidates.FirstOrDefault(x => x.Id == reviewerId);
        if (chosen.Id is null)
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, "Choose a reviewer or delegate configured for this category.");
        }

        var o = options.Value;
        var policy = o.ToSlaPolicy();
        var due = policy.VerificationDue(ctx.NowUtc, o.ToCalendar());
        var assignment = new ReviewerAssignment(chosen.Id, chosen.Name, [], due, policy.ReminderAt(ctx.NowUtc, due));
        c.Reassign(assignment, reason, ctx);
        NotifyReviewer(c, assignment);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ReturnToVendorAsync(Guid caseId, string reason, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        c.ReturnToVendor(reason, Ctx(actor));
        if (!string.IsNullOrWhiteSpace(c.SenderEmail))
        {
            outbox.Enqueue(NotificationTemplates.ReturnedToVendor(c, c.SenderEmail, reason));
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task VerifyAsync(Guid caseId, IReadOnlyCollection<ChecklistResponse> answers, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        c.Verify(answers, _checklist, Ctx(actor));
        if (!string.IsNullOrWhiteSpace(options.Value.AccountsPayableEmail))
        {
            outbox.Enqueue(NotificationTemplates.ReadyForAccountsPayable(c, options.Value.AccountsPayableEmail, options.Value.WorkbenchUrl));
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AcceptForProcessingAsync(Guid caseId, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        c.AcceptForProcessing(Ctx(actor));
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SendBackToReviewerAsync(Guid caseId, string reason, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        var ctx = Ctx(actor);
        var assignment = await BuildAssignmentAsync(c.CategoryCode, ctx.NowUtc, ct).ConfigureAwait(false);
        c.SendBackToReviewer(reason, assignment, ctx);
        NotifyReviewer(c, assignment);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordErpEntryAsync(Guid caseId, string erpDocumentNumber, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        c.RecordErpEntry(erpDocumentNumber, Ctx(actor));
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task CancelAsync(Guid caseId, string reason, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        c.Cancel(reason, Ctx(actor));
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ResolveFlagAsync(Guid caseId, Guid flagId, string resolution, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        c.ResolveFlag(flagId, resolution, Ctx(actor));
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------

    private async Task CheckDuplicatesAsync(InvoiceCase c, ActionContext ctx, CancellationToken ct)
    {
        var existing = await cases.GetFingerprintsAsync(
            c.VendorNo,
            DuplicateDetector.NormaliseKey(c.VendorName),
            DuplicateDetector.NormaliseInvoiceNumber(c.InvoiceNumber),
            ct).ConfigureAwait(false);
        var candidate = new InvoiceFingerprint(c.Id, c.Reference, c.VendorNo, c.VendorName, c.InvoiceNumber ?? string.Empty, c.Amount, c.Currency, c.InvoiceDate);
        var matches = DuplicateDetector.FindMatches(candidate, existing);
        if (matches.Count > 0)
        {
            var list = string.Join("; ", matches.Select(m => $"{m.ExistingReference} ({m.Kind})"));
            c.RaiseFlag(FlagCodes.PossibleDuplicate, $"Possible duplicate of: {list}.", blocking: true, ctx);
        }
    }

    /// <summary>Phase 2: read-only validation against Business Central. Raises flags; never blocks registration itself.</summary>
    private async Task CheckAgainstErpAsync(InvoiceCase c, ActionContext ctx, CancellationToken ct)
    {
        if (!erp.IsEnabled)
        {
            return;
        }

        var vendor = await erp.GetVendorAsync(c.VendorNo!, ct).ConfigureAwait(false);
        if (vendor is null || vendor.Blocked)
        {
            c.RaiseFlag(FlagCodes.ErpValidationFailed, vendor is null ? $"Vendor {c.VendorNo} not found in Business Central." : $"Vendor {c.VendorNo} is blocked in Business Central.", blocking: true, ctx);
            return;
        }

        var order = await erp.CheckOrderAsync(c.VendorNo!, c.OrderNumber!, ct).ConfigureAwait(false);
        var problems = new List<string>();
        if (!order.OrderFound)
        {
            problems.Add($"order {c.OrderNumber} not found");
        }
        else
        {
            if (!order.BelongsToVendor)
            {
                problems.Add("order belongs to a different vendor");
            }

            if (!order.IsOpen)
            {
                problems.Add("order is not open");
            }

            if (!order.HasPostedReceipt)
            {
                problems.Add("no posted receipt (GRV) against the order");
            }

            if (order.Currency is not null && !string.Equals(order.Currency, c.Currency, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"currency differs from order ({order.Currency})");
            }
        }

        if (problems.Count > 0)
        {
            c.RaiseFlag(FlagCodes.ErpValidationFailed, "Business Central: " + string.Join("; ", problems) + ".", blocking: true, ctx);
        }

        if (order.RemainingAmount is { } remaining && c.Amount > remaining)
        {
            c.RaiseFlag(FlagCodes.AmountExceedsOrderBalance,
                string.Create(CultureInfo.InvariantCulture, $"Invoice amount {c.Amount:N2} exceeds the remaining order balance {remaining:N2}."),
                blocking: true, ctx);
        }

        // Bank details: compare only. Business Central is the master; never update it from an invoice.
        if (vendor.BankAccountNumbers.Count == 0)
        {
            c.RaiseFlag(FlagCodes.BankDetailsUnverified,
                "Business Central returned no bank account for this vendor, so the invoice's bank details could not be compared. AP must pay only to the vendor master account.",
                blocking: false, ctx);
        }
        else if (c.ExtractedJson is not null)
        {
            var extracted = System.Text.Json.JsonSerializer.Deserialize<ExtractedInvoiceFields>(c.ExtractedJson, WebJson);
            var invoiceAccount = Digits(extracted?.BankAccountNumber);
            if (invoiceAccount.Length >= 6 && !vendor.BankAccountNumbers.Select(Digits).Contains(invoiceAccount))
            {
                c.RaiseFlag(FlagCodes.BankDetailsMismatch,
                    "The bank account on the invoice does not match any account on the Business Central vendor record. Verify by phone through a known contact before payment.",
                    blocking: true, ctx);
            }
        }
    }

    private static string Digits(string? s) => s is null ? string.Empty : new string(s.Where(char.IsDigit).ToArray());

    private async Task<ReviewerAssignment> BuildAssignmentAsync(string? categoryCode, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var route = categoryCode is null ? null : await routes.GetAsync(categoryCode, ct).ConfigureAwait(false);
        if (route is not { Active: true })
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, $"No active route is configured for category '{categoryCode}'.");
        }

        var o = options.Value;
        var policy = o.ToSlaPolicy();
        var due = policy.VerificationDue(nowUtc, o.ToCalendar());
        return new ReviewerAssignment(route.ReviewerId, route.ReviewerName, route.DelegateIdList, due, policy.ReminderAt(nowUtc, due));
    }

    private void NotifyReviewer(InvoiceCase c, ReviewerAssignment a)
    {
        // Reviewer ids are Entra object ids; the dispatcher resolves them to a mailbox via Graph.
        outbox.Enqueue(NotificationTemplates.AssignedToReviewer(c, a.ReviewerId, options.Value.WorkbenchUrl));
    }

    private async Task<InvoiceCase> LoadAsync(Guid id, CancellationToken ct) =>
        await cases.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Case {id} was not found.");

    private ActionContext Ctx(Actor actor) => ActionContext.For(actor, clock.UtcNow);
}
