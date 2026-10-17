using System.Text.Json;
using Desicon.Vim.Domain.Audit;
using Desicon.Vim.Domain.Checklists;
using Desicon.Vim.Domain.Workflow;

namespace Desicon.Vim.Domain.Cases;

/// <summary>
/// One vendor invoice package from receipt to payment. All changes go through
/// the methods below; each one checks the workflow rules, applies the change
/// and appends an audit event. Properties have private setters so nothing
/// outside this class can change a case without leaving a trace.
/// </summary>
public sealed class InvoiceCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly List<CaseDocument> _documents = [];
    private readonly List<CaseChecklistAnswer> _checklistAnswers = [];
    private readonly List<CaseFlag> _flags = [];
    private readonly List<AuditEvent> _newAuditEvents = [];

    private InvoiceCase()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Desicon tracking reference, e.g. VIM-2026-000123.</summary>
    public string Reference { get; private set; } = string.Empty;

    /// <summary>Case type key so the platform can host other document workflows later.</summary>
    public string CaseType { get; private set; } = "VENDOR_INVOICE";

    public CaseStatus Status { get; private set; }

    public SubmissionChannel Channel { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public string? SenderEmail { get; private set; }

    public string? SenderName { get; private set; }

    public string? SubmissionSubject { get; private set; }

    public int SubmissionRound { get; private set; }

    // ---- Confirmed details (set at registration) ----
    public string? VendorName { get; private set; }

    public string? VendorNo { get; private set; }

    public string? InvoiceNumber { get; private set; }

    public DateOnly? InvoiceDate { get; private set; }

    public decimal? Amount { get; private set; }

    public string? Currency { get; private set; }

    public string? OrderNumber { get; private set; }

    public SupplyType SupplyType { get; private set; }

    public string? CategoryCode { get; private set; }

    // ---- Suggested by Document Intelligence ----
    public string? ExtractedJson { get; private set; }

    // ---- People ----
    public string? RegisteredById { get; private set; }

    public string? AssignedReviewerId { get; private set; }

    public string? AssignedReviewerName { get; private set; }

    /// <summary>Comma-separated Entra ids allowed to act for the assigned reviewer.</summary>
    public string ReviewerDelegateIds { get; private set; } = string.Empty;

    public string? VerifiedById { get; private set; }

    public string? ApProcessedById { get; private set; }

    // ---- Deadlines ----
    public DateTimeOffset? DueAtUtc { get; private set; }

    public DateTimeOffset? ReminderAtUtc { get; private set; }

    public bool ReminderSent { get; private set; }

    public DateTimeOffset? EscalatedAtUtc { get; private set; }

    // ---- ERP ----
    public string? ErpDocumentNumber { get; private set; }

    public string? PaymentReference { get; private set; }

    public DateOnly? PaidOn { get; private set; }

    public string? LastReason { get; private set; }

    public DateTimeOffset LastChangedAtUtc { get; private set; }

    /// <summary>Optimistic concurrency token (two people acting at once: the second gets a clear error).</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Last audit hash, so new events can be chained without loading the whole history.</summary>
    public string LastAuditHash { get; private set; } = AuditEvent.GenesisHash;

    public int LastAuditSequence { get; private set; }

    public IReadOnlyList<CaseDocument> Documents => _documents;

    public IReadOnlyList<CaseChecklistAnswer> ChecklistAnswers => _checklistAnswers;

    public IReadOnlyList<CaseFlag> Flags => _flags;

    /// <summary>Audit events created by this unit of work, not yet saved.</summary>
    public IReadOnlyList<AuditEvent> NewAuditEvents => _newAuditEvents;

    public bool HasOpenBlockingFlags => _flags.Any(f => f.IsOpen && f.Blocking);

    public bool IsOverdue(DateTimeOffset nowUtc) => DueAtUtc is { } due && nowUtc > due && !WorkflowRules.IsTerminal(Status);

    public IReadOnlyList<string> DelegateIdList =>
        ReviewerDelegateIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // =====================================================================
    // Creation
    // =====================================================================

    /// <summary>A new package has arrived (email, physical or portal).</summary>
    public static InvoiceCase Receive(
        string reference,
        SubmissionChannel channel,
        string? senderEmail,
        string? senderName,
        string? subject,
        ActionContext ctx)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(ctx);

        if (channel == SubmissionChannel.Physical && !ctx.Actor.HasRole(VimRole.DocumentController))
        {
            throw new DomainRuleException(RuleCodes.RoleNotPermitted, "Only a Document Controller can register a physical submission.");
        }

        var c = new InvoiceCase
        {
            Id = Guid.CreateVersion7(ctx.NowUtc),
            Reference = reference,
            Status = CaseStatus.Received,
            Channel = channel,
            ReceivedAtUtc = ctx.NowUtc,
            SenderEmail = senderEmail?.Trim().ToLowerInvariant(),
            SenderName = senderName,
            SubmissionSubject = subject,
            SubmissionRound = 1,
            LastChangedAtUtc = ctx.NowUtc,
        };
        c.Audit("Received", null, CaseStatus.Received, ctx, null, new { channel, senderEmail, subject });
        return c;
    }

    // =====================================================================
    // Documents, extraction, flags (no status change)
    // =====================================================================

    public CaseDocument AttachDocument(DocumentKind kind, string fileName, string contentType, long sizeBytes, string sha256, string blobPath, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EnsureNotTerminal();
        if (sizeBytes <= 0)
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, "Empty files cannot be attached.");
        }

        var doc = CaseDocument.Create(Id, kind, fileName, contentType, sizeBytes, sha256, blobPath, SubmissionRound, ctx);
        _documents.Add(doc);
        Audit("DocumentAttached", Status, Status, ctx, null, new { doc.Id, kind, fileName, sizeBytes, sha256, round = SubmissionRound });
        return doc;
    }

    public void RecordScanResult(Guid documentId, ScanStatus status, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var doc = _documents.SingleOrDefault(d => d.Id == documentId)
            ?? throw new DomainRuleException(RuleCodes.InvalidValue, "Document not found on this case.");
        doc.SetScanStatus(status);
        Audit("DocumentScanned", Status, Status, ctx, null, new { documentId, status });
        if (status == ScanStatus.Malicious)
        {
            RaiseFlag(FlagCodes.MaliciousAttachment, $"Malware detected in {doc.FileName}.", blocking: true, ctx);
        }
    }

    public void RecordExtraction(ExtractedInvoiceFields fields, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(ctx);
        ExtractedJson = JsonSerializer.Serialize(fields, Json);
        Audit("FieldsExtracted", Status, Status, ctx, null, new { fields.InvoiceNumber, fields.Amount, fields.Currency, fields.Confidence });
    }

    public CaseFlag RaiseFlag(string code, string description, bool blocking, ActionContext ctx)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(ctx);
        var existing = _flags.FirstOrDefault(f => f.IsOpen && f.Code == code);
        if (existing is not null)
        {
            return existing;
        }

        var flag = CaseFlag.Create(Id, code, description, blocking, ctx);
        _flags.Add(flag);
        Audit("FlagRaised", Status, Status, ctx, null, new { flag.Id, code, description, blocking });
        return flag;
    }

    public void ResolveFlag(Guid flagId, string resolution, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        RequireReason(resolution);
        var flag = _flags.SingleOrDefault(f => f.Id == flagId && f.IsOpen)
            ?? throw new DomainRuleException(RuleCodes.InvalidValue, "Open flag not found on this case.");

        var fmOnly = FlagCodes.RequiresFinanceManager(flag);
        var permitted = fmOnly
            ? ctx.Actor.HasRole(VimRole.FinanceManager)
            : ctx.Actor.HasAnyRole([VimRole.FinanceManager, VimRole.DocumentController, VimRole.AccountsPayable]);
        if (!permitted)
        {
            throw new DomainRuleException(RuleCodes.RoleNotPermitted, fmOnly
                ? $"Only the Finance Manager can clear a blocking {flag.Code} flag."
                : "You are not permitted to clear this flag.");
        }

        if (ctx.Actor.Id == RegisteredById || ctx.Actor.Id == ApProcessedById)
        {
            throw new DomainRuleException(RuleCodes.SegregationOfDuties, "The person who registered or is processing this case cannot clear its flags.");
        }

        flag.Resolve(resolution, ctx);
        Audit("FlagResolved", Status, Status, ctx, resolution, new { flagId, flag.Code });
    }

    // =====================================================================
    // Workflow actions
    // =====================================================================

    public void Register(InvoiceDetails details, IReadOnlyCollection<ChecklistResponse> intakeAnswers, ChecklistDefinition checklist, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(checklist);
        var t = WorkflowRules.Resolve(CaseAction.Register, Status, ctx.Actor);

        var problems = details.Problems();
        if (problems.Count > 0)
        {
            throw new DomainRuleException(RuleCodes.MissingDetails, string.Join(" ", problems));
        }

        if (_documents.Count == 0)
        {
            throw new DomainRuleException(RuleCodes.MissingDetails, "At least one document must be attached before registration.");
        }

        if (_documents.Any(d => d.ScanStatus is ScanStatus.Malicious or ScanStatus.ScanFailed))
        {
            throw new DomainRuleException(RuleCodes.BlockingFlagOpen, "A document failed or could not complete the malware scan. Return the package to the vendor.");
        }

        EnsureChecklistPassed(checklist, ChecklistStage.Intake, details.SupplyType, intakeAnswers, "intake");

        VendorName = details.VendorName.Trim();
        VendorNo = details.VendorNo?.Trim();
        InvoiceNumber = details.InvoiceNumber.Trim();
        InvoiceDate = details.InvoiceDate;
        Amount = details.Amount;
        Currency = details.Currency.Trim().ToUpperInvariant();
        OrderNumber = details.OrderNumber.Trim();
        SupplyType = details.SupplyType;
        CategoryCode = details.CategoryCode;
        RegisteredById = ctx.Actor.Id;

        SaveAnswers(checklist, ChecklistStage.Intake, intakeAnswers, ctx);
        Move(t, ctx, null, new { details.VendorName, details.InvoiceNumber, details.Amount, details.Currency, details.OrderNumber, details.CategoryCode, checklist = checklist.Version });
    }

    public void ReturnToVendor(string reason, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var t = WorkflowRules.Resolve(CaseAction.ReturnToVendor, Status, ctx.Actor);
        RequireReason(reason);
        if (Status == CaseStatus.UnderVerification)
        {
            EnsureAssignedReviewer(ctx.Actor);
        }

        ClearDeadline();
        Move(t, ctx, reason, null);
    }

    public void Resubmit(string? subject, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var t = WorkflowRules.Resolve(CaseAction.Resubmit, Status, ctx.Actor);
        SubmissionRound++;
        SubmissionSubject = subject ?? SubmissionSubject;
        ExtractedJson = null; // fields read from the previous round must not be reused

        // Earlier verification no longer applies to the corrected package.
        AssignedReviewerId = null;
        AssignedReviewerName = null;
        ReviewerDelegateIds = string.Empty;
        VerifiedById = null;
        Move(t, ctx, null, new { round = SubmissionRound, subject });
    }

    public void SendForVerification(ReviewerAssignment assignment, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var t = WorkflowRules.Resolve(CaseAction.SendForVerification, Status, ctx.Actor);
        EnsureIndependentReviewer(assignment, ctx.Actor);
        Assign(assignment);
        Move(t, ctx, null, new { assignment.ReviewerId, assignment.ReviewerName, assignment.DueAtUtc });
    }

    public void Reassign(ReviewerAssignment assignment, string reason, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var t = WorkflowRules.Resolve(CaseAction.Reassign, Status, ctx.Actor);
        RequireReason(reason);
        EnsureIndependentReviewer(assignment, ctx.Actor);
        var previous = AssignedReviewerId;
        Assign(assignment);
        Move(t, ctx, reason, new { previous, assignment.ReviewerId, assignment.ReviewerName, assignment.DueAtUtc });
    }

    public void Verify(IReadOnlyCollection<ChecklistResponse> verificationAnswers, ChecklistDefinition checklist, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        var t = WorkflowRules.Resolve(CaseAction.Verify, Status, ctx.Actor);
        EnsureAssignedReviewer(ctx.Actor);

        if (ctx.Actor.Id == RegisteredById)
        {
            throw new DomainRuleException(RuleCodes.SegregationOfDuties, "The person who registered a case cannot also verify it.");
        }

        EnsureChecklistPassed(checklist, ChecklistStage.Verification, SupplyType, verificationAnswers, "verification");

        VerifiedById = ctx.Actor.Id;
        SaveAnswers(checklist, ChecklistStage.Verification, verificationAnswers, ctx);
        ClearDeadline();
        Move(t, ctx, null, new { checklist = checklist.Version });
    }

    public void AcceptForProcessing(ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var t = WorkflowRules.Resolve(CaseAction.AcceptForProcessing, Status, ctx.Actor);
        EnsureIndependentOfEarlierSteps(ctx.Actor, "accept it for processing");
        EnsureReadyForPayment();

        ApProcessedById = ctx.Actor.Id;
        Move(t, ctx, null, null);
    }

    public void SendBackToReviewer(string reason, ReviewerAssignment assignment, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var t = WorkflowRules.Resolve(CaseAction.SendBackToReviewer, Status, ctx.Actor);
        RequireReason(reason);
        EnsureIndependentReviewer(assignment, ctx.Actor);
        VerifiedById = null;
        ApProcessedById = null;
        Assign(assignment);
        Move(t, ctx, reason, new { assignment.ReviewerId, assignment.DueAtUtc });
    }

    public void RecordErpEntry(string erpDocumentNumber, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var t = WorkflowRules.Resolve(CaseAction.RecordErpEntry, Status, ctx.Actor);
        if (string.IsNullOrWhiteSpace(erpDocumentNumber))
        {
            throw new DomainRuleException(RuleCodes.MissingDetails, "Business Central document number is required.");
        }

        EnsureIndependentOfEarlierSteps(ctx.Actor, "record its ERP entry");
        EnsureReadyForPayment();
        ErpDocumentNumber = erpDocumentNumber.Trim();
        Move(t, ctx, null, new { erpDocumentNumber = ErpDocumentNumber });
    }

    public void RecordPayment(string paymentReference, DateOnly paidOn, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var t = WorkflowRules.Resolve(CaseAction.RecordPayment, Status, ctx.Actor);
        if (string.IsNullOrWhiteSpace(paymentReference))
        {
            throw new DomainRuleException(RuleCodes.MissingDetails, "Payment reference is required.");
        }

        PaymentReference = paymentReference.Trim();
        PaidOn = paidOn;
        Move(t, ctx, null, new { paymentReference, paidOn });
    }

    public void Close(ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var t = WorkflowRules.Resolve(CaseAction.Close, Status, ctx.Actor);
        Move(t, ctx, null, null);
    }

    public void Cancel(string reason, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var t = WorkflowRules.Resolve(CaseAction.Cancel, Status, ctx.Actor);
        RequireReason(reason);
        ClearDeadline();
        Move(t, ctx, reason, null);
    }

    // =====================================================================
    // Deadlines
    // =====================================================================

    public void MarkReminderSent(ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ReminderSent = true;
        Audit("ReminderSent", Status, Status, ctx, null, new { AssignedReviewerId, DueAtUtc });
    }

    public void MarkEscalated(string escalatedTo, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EscalatedAtUtc = ctx.NowUtc;
        Audit("Escalated", Status, Status, ctx, null, new { escalatedTo, AssignedReviewerId, DueAtUtc });
    }

    /// <summary>For the UI: which buttons this person should see right now.</summary>
    public IReadOnlyList<CaseAction> AvailableActionsFor(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return WorkflowRules.AvailableActions(Status, actor)
            .Where(a => a switch
            {
                CaseAction.Verify => IsAssignedReviewer(actor) && actor.Id != RegisteredById,
                CaseAction.ReturnToVendor when Status == CaseStatus.UnderVerification => IsAssignedReviewer(actor),
                CaseAction.AcceptForProcessing or CaseAction.RecordErpEntry => IsIndependentOfEarlierSteps(actor),
                _ => true,
            })
            .ToList();
    }

    public void ClearNewAuditEvents() => _newAuditEvents.Clear();

    // =====================================================================
    // Internals
    // =====================================================================

    public bool IsAssignedReviewer(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.HasRole(VimRole.Reviewer)
            && (actor.Id == AssignedReviewerId || DelegateIdList.Contains(actor.Id, StringComparer.Ordinal));
    }

    private void Assign(ReviewerAssignment a)
    {
        AssignedReviewerId = a.ReviewerId;
        AssignedReviewerName = a.ReviewerName;
        ReviewerDelegateIds = string.Join(',', a.DelegateIds);
        DueAtUtc = a.DueAtUtc;
        ReminderAtUtc = a.ReminderAtUtc;
        ReminderSent = false;
        EscalatedAtUtc = null;
    }

    private void ClearDeadline()
    {
        DueAtUtc = null;
        ReminderAtUtc = null;
        ReminderSent = false;
        EscalatedAtUtc = null;
    }

    private void EnsureAssignedReviewer(Actor actor)
    {
        if (!IsAssignedReviewer(actor))
        {
            throw new DomainRuleException(RuleCodes.NotAssignedReviewer, $"Only the assigned reviewer ({AssignedReviewerName}) or their delegate can act on this case.");
        }
    }

    /// <summary>People who cleared a blocking flag on this case.</summary>
    public IReadOnlyList<string> BlockingFlagResolverIds =>
        _flags.Where(f => f.Blocking && f.ResolvedById is not null).Select(f => f.ResolvedById!).Distinct().ToList();

    /// <summary>AP must be someone other than the registrar, the verifier and anyone who cleared a blocking flag.</summary>
    public bool IsIndependentOfEarlierSteps(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.Id != RegisteredById && actor.Id != VerifiedById && !BlockingFlagResolverIds.Contains(actor.Id);
    }

    private void EnsureIndependentOfEarlierSteps(Actor actor, string what)
    {
        if (!IsIndependentOfEarlierSteps(actor))
        {
            throw new DomainRuleException(RuleCodes.SegregationOfDuties, $"Someone who registered, verified or cleared a blocking flag on this case cannot also {what}.");
        }
    }

    private void EnsureIndependentReviewer(ReviewerAssignment assignment, Actor actor)
    {
        if (assignment.ReviewerId == actor.Id || assignment.ReviewerId == RegisteredById
            || assignment.DelegateIds.Contains(actor.Id, StringComparer.Ordinal))
        {
            throw new DomainRuleException(RuleCodes.SegregationOfDuties, "A case cannot be assigned to the person routing it or to the person who registered it.");
        }
    }

    /// <summary>Before AP touches a case: no open blocking flags and every document scanned clean.</summary>
    private void EnsureReadyForPayment()
    {
        if (HasOpenBlockingFlags)
        {
            var codes = string.Join(", ", _flags.Where(f => f.IsOpen && f.Blocking).Select(f => f.Code));
            throw new DomainRuleException(RuleCodes.BlockingFlagOpen, $"Blocking flags must be cleared first: {codes}.");
        }

        if (_documents.Any(d => d.ScanStatus != ScanStatus.Clean))
        {
            throw new DomainRuleException(RuleCodes.BlockingFlagOpen, "Every document must pass the malware scan before Accounts Payable can process the case.");
        }
    }

    private void EnsureNotTerminal()
    {
        if (WorkflowRules.IsTerminal(Status))
        {
            throw new DomainRuleException(RuleCodes.ActionNotAllowedInStatus, $"The case is {Status}; no further changes are allowed.");
        }
    }

    private static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
        {
            throw new DomainRuleException(RuleCodes.ReasonRequired, "A reason of at least 5 characters is required.");
        }
    }

    private static void EnsureChecklistPassed(ChecklistDefinition checklist, ChecklistStage stage, SupplyType supply, IReadOnlyCollection<ChecklistResponse> answers, string label)
    {
        var result = checklist.Evaluate(stage, supply, answers);
        if (!result.IsComplete)
        {
            throw new DomainRuleException(RuleCodes.ChecklistIncomplete, $"The {label} checklist is incomplete: {string.Join(", ", result.Unanswered)}.");
        }

        if (!result.Passed)
        {
            throw new DomainRuleException(RuleCodes.ChecklistFailed, $"The {label} checklist has failed items ({string.Join(", ", result.Failed)}). Return the case instead.");
        }
    }

    private void SaveAnswers(ChecklistDefinition checklist, ChecklistStage stage, IReadOnlyCollection<ChecklistResponse> answers, ActionContext ctx)
    {
        foreach (var a in answers)
        {
            _checklistAnswers.Add(CaseChecklistAnswer.Create(Id, checklist.Version, stage, a, ctx));
        }
    }

    private void Move(Transition t, ActionContext ctx, string? reason, object? details)
    {
        var from = Status;
        var to = t.To ?? Status;
        Status = to;
        LastReason = reason ?? LastReason;
        LastChangedAtUtc = ctx.NowUtc;
        Audit(t.Action.ToString(), from, to, ctx, reason, details);
    }

    private void Audit(string action, CaseStatus? from, CaseStatus? to, ActionContext ctx, string? reason, object? details)
    {
        var e = AuditEvent.Create(
            Id,
            LastAuditSequence + 1,
            action,
            from,
            to,
            ctx.Actor,
            ctx.NowUtc,
            reason,
            details is null ? null : JsonSerializer.Serialize(details, Json),
            ctx.CorrelationId,
            LastAuditHash);
        _newAuditEvents.Add(e);
        LastAuditSequence = e.Sequence;
        LastAuditHash = e.Hash;
    }
}
