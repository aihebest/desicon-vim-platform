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

    /// <summary>When the goods were delivered or the service completed (from the waybill / WCC). Start of the 3-working-day target.</summary>
    public DateOnly? DeliveredOn { get; private set; }

    // ---- Suggested by Document Intelligence ----
    public string? ExtractedJson { get; private set; }

    // ---- People ----

    /// <summary>Who submitted: the vendor's portal identity, or the Accounts Payable officer who keyed it in (assisted).</summary>
    public string? RegisteredById { get; private set; }

    public string? AssignedReviewerId { get; private set; }

    public string? AssignedReviewerName { get; private set; }

    /// <summary>Comma-separated Entra ids allowed to act for the assigned reviewer.</summary>
    public string ReviewerDelegateIds { get; private set; } = string.Empty;

    public string? VerifiedById { get; private set; }

    /// <summary>The Accounts officer who accepted the case.</summary>
    public string? ApProcessedById { get; private set; }

    /// <summary>
    /// Official submission time: when Accounts accepted the case and the vendor was told.
    /// The vendor's credit period counts from here (DMD, 19 Oct 2026).
    /// </summary>
    public DateTimeOffset? AcceptedAtUtc { get; private set; }

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

    /// <summary>
    /// A vendor submission with its details (portal, or assisted by Accounts Payable).
    /// The automatic checks then decide where it goes.
    /// </summary>
    public static InvoiceCase Submit(string reference, SubmissionChannel channel, InvoiceSubmission submission, ActionContext ctx)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(ctx);

        var allowed = channel switch
        {
            SubmissionChannel.Portal => ctx.Actor.HasRole(VimRole.Vendor),
            SubmissionChannel.Assisted => ctx.Actor.HasRole(VimRole.AccountsPayable),
            _ => false,
        };
        if (!allowed)
        {
            throw new DomainRuleException(RuleCodes.RoleNotPermitted, channel == SubmissionChannel.Assisted
                ? "Only Accounts Payable can submit an invoice on a vendor's behalf."
                : $"{ctx.Actor.DisplayName} cannot submit through the {channel} channel.");
        }

        var problems = submission.Problems(DateOnly.FromDateTime(ctx.NowUtc.ToOffset(TimeSpan.FromHours(1)).DateTime));
        if (problems.Count > 0)
        {
            throw new DomainRuleException(RuleCodes.MissingDetails, string.Join(" ", problems));
        }

        var c = new InvoiceCase
        {
            Id = Guid.CreateVersion7(ctx.NowUtc),
            Reference = reference,
            Status = CaseStatus.Received,
            Channel = channel,
            ReceivedAtUtc = ctx.NowUtc,
            SubmissionRound = 1,
            LastChangedAtUtc = ctx.NowUtc,
            RegisteredById = ctx.Actor.Id,
        };
        c.ApplySubmission(submission);
        c.Audit("Submitted", null, CaseStatus.Received, ctx, null, new
        {
            channel,
            submission.VendorNo,
            submission.InvoiceNumber,
            submission.Amount,
            submission.Currency,
            submission.OrderNumber,
            submission.DeliveredOn,
        });
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
            : ctx.Actor.HasAnyRole([VimRole.FinanceManager, VimRole.AccountsPayable]);
        if (!permitted)
        {
            throw new DomainRuleException(RuleCodes.RoleNotPermitted, fmOnly
                ? $"Only the Finance Manager can clear a blocking {flag.Code} flag."
                : "You are not permitted to clear this flag.");
        }

        if (ctx.Actor.Id == RegisteredById || ctx.Actor.Id == ApProcessedById)
        {
            throw new DomainRuleException(RuleCodes.SegregationOfDuties, "The person who submitted or accepted this case cannot clear its flags.");
        }

        flag.Resolve(resolution, ctx);
        Audit("FlagResolved", Status, Status, ctx, resolution, new { flagId, flag.Code });
    }

    // =====================================================================
    // Workflow actions (v2: vendor submits -> automatic checks -> Accounts)
    // =====================================================================

    /// <summary>System: every automatic check passed. Accounts must confirm by the deadline.</summary>
    public void PassToAccounts(StageDeadline deadline, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        var t = WorkflowRules.Resolve(CaseAction.PassToAccounts, Status, ctx.Actor);
        EnsureCompleteAndClean();
        EnsureNoBlockingFlags();
        SetDeadline(deadline);
        Move(t, ctx, null, new { deadline.DueAtUtc });
    }

    /// <summary>System: an automatic check raised a blocking flag that needs the Finance Manager's decision.</summary>
    public void RaiseException(StageDeadline deadline, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        var t = WorkflowRules.Resolve(CaseAction.RaiseException, Status, ctx.Actor);
        if (!HasOpenBlockingFlags)
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, "An exception needs at least one open blocking flag.");
        }

        SetDeadline(deadline);
        Move(t, ctx, null, new { flags = _flags.Where(f => f.IsOpen && f.Blocking).Select(f => f.Code), deadline.DueAtUtc });
    }

    /// <summary>Finance Manager: the flags are cleared; send the case on to Accounts.</summary>
    public void ReleaseToAccounts(StageDeadline deadline, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        var t = WorkflowRules.Resolve(CaseAction.ReleaseToAccounts, Status, ctx.Actor);
        if (ctx.Actor.Id == RegisteredById)
        {
            throw new DomainRuleException(RuleCodes.SegregationOfDuties, "The person who submitted this invoice cannot release it.");
        }

        EnsureNoBlockingFlags();
        EnsureCompleteAndClean();
        SetDeadline(deadline);
        Move(t, ctx, null, new { deadline.DueAtUtc });
    }

    /// <summary>
    /// Rejected with reasons: automatically (system, at the checks), by the Finance Manager
    /// (exception) or by Accounts (confirmatory check). No credit days run while returned.
    /// </summary>
    public void ReturnToVendor(string reason, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var t = WorkflowRules.Resolve(CaseAction.ReturnToVendor, Status, ctx.Actor);
        RequireReason(reason);
        ClearDeadline();
        Move(t, ctx, reason, null);
    }

    /// <summary>
    /// Corrected details against the same reference. New documents are attached
    /// afterwards (round + 1); the automatic checks then run again from the start.
    /// </summary>
    public void Resubmit(InvoiceSubmission corrected, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(corrected);
        ArgumentNullException.ThrowIfNull(ctx);
        var t = WorkflowRules.Resolve(CaseAction.Resubmit, Status, ctx.Actor);
        var problems = corrected.Problems(DateOnly.FromDateTime(ctx.NowUtc.ToOffset(TimeSpan.FromHours(1)).DateTime));
        if (problems.Count > 0)
        {
            throw new DomainRuleException(RuleCodes.MissingDetails, string.Join(" ", problems));
        }

        if (!string.Equals(corrected.VendorNo.Trim(), VendorNo, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, "A resubmission must be for the same vendor.");
        }

        SubmissionRound++;
        ApplySubmission(corrected);
        RegisteredById = ctx.Actor.Id;

        // Flags the automatic checks raised were about the earlier round; the checks raise them again if still true.
        foreach (var flag in _flags.Where(f => f.IsOpen && f.RaisedById == Actor.System.Id).ToList())
        {
            flag.Resolve($"Superseded by resubmission round {SubmissionRound}; the automatic checks run again.", ctx);
            Audit("FlagResolved", Status, Status, ctx, "Superseded by resubmission", new { flagId = flag.Id, flag.Code });
        }

        ExtractedJson = null;
        AcceptedAtUtc = null;
        ApProcessedById = null;
        Move(t, ctx, null, new { round = SubmissionRound, corrected.InvoiceNumber, corrected.Amount, corrected.OrderNumber });
    }

    /// <summary>Legacy email intake: a reply quoting the reference reopens a returned case.</summary>
    public void ResubmitByEmail(string? subject, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var t = WorkflowRules.Resolve(CaseAction.Resubmit, Status, ctx.Actor);
        SubmissionRound++;
        SubmissionSubject = subject ?? SubmissionSubject;
        ExtractedJson = null;
        Move(t, ctx, null, new { round = SubmissionRound, subject });
    }

    /// <summary>
    /// Accounts' confirmatory check. On success this is the official submission:
    /// the vendor is told and the credit period starts now.
    /// </summary>
    public void Accept(IReadOnlyCollection<ChecklistResponse> answers, ChecklistDefinition checklist, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        var t = WorkflowRules.Resolve(CaseAction.Accept, Status, ctx.Actor);
        EnsureIndependentOfEarlierSteps(ctx.Actor, "accept it");
        EnsureNoBlockingFlags();
        EnsureCompleteAndClean();
        EnsureChecklistPassed(checklist, ChecklistStage.AccountsCheck, SupplyType, answers, "Accounts");

        SaveAnswers(checklist, ChecklistStage.AccountsCheck, answers, ctx);
        ApProcessedById = ctx.Actor.Id;
        AcceptedAtUtc = ctx.NowUtc;
        ClearDeadline();
        Move(t, ctx, null, new { checklist = checklist.Version, acceptedAtUtc = AcceptedAtUtc });
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
                CaseAction.Accept or CaseAction.RecordErpEntry => IsIndependentOfEarlierSteps(actor),
                CaseAction.ReleaseToAccounts => actor.Id != RegisteredById,
                _ => true,
            })
            .ToList();
    }

    /// <summary>
    /// The documents that count now: for each kind, those from the latest round that
    /// supplied that kind. A corrected file in round 2 replaces the round-1 file.
    /// </summary>
    public IReadOnlyList<CaseDocument> CurrentDocuments =>
        _documents
            .GroupBy(d => d.Kind)
            .SelectMany(g =>
            {
                var latest = g.Max(d => d.SubmissionRound);
                return g.Where(d => d.SubmissionRound == latest);
            })
            .ToList();

    /// <summary>Required slots with no document (empty for legacy email/physical cases).</summary>
    public IReadOnlyList<DocumentKind> MissingRequiredDocuments =>
        Channel is SubmissionChannel.Portal or SubmissionChannel.Assisted
            ? RequiredDocuments.For(SupplyType).Where(k => !_documents.Any(d => d.Kind == k)).ToList()
            : [];

    public bool AllCurrentDocumentsScanned => CurrentDocuments.All(d => d.ScanStatus != ScanStatus.Pending);

    public void ClearNewAuditEvents() => _newAuditEvents.Clear();

    // =====================================================================
    // Internals
    // =====================================================================

    private void SetDeadline(StageDeadline d)
    {
        AssignedReviewerId = null;
        AssignedReviewerName = d.HolderName;
        ReviewerDelegateIds = string.Empty;
        DueAtUtc = d.DueAtUtc;
        ReminderAtUtc = d.ReminderAtUtc;
        ReminderSent = false;
        EscalatedAtUtc = null;
    }

    private void ClearDeadline()
    {
        AssignedReviewerName = null;
        DueAtUtc = null;
        ReminderAtUtc = null;
        ReminderSent = false;
        EscalatedAtUtc = null;
    }

    private void ApplySubmission(InvoiceSubmission s)
    {
        VendorNo = s.VendorNo.Trim().ToUpperInvariant();
        VendorName = s.VendorName.Trim();
        SenderName = VendorName;
        SenderEmail = string.IsNullOrWhiteSpace(s.VendorEmail) ? SenderEmail : s.VendorEmail.Trim().ToLowerInvariant();
        InvoiceNumber = s.InvoiceNumber.Trim();
        InvoiceDate = s.InvoiceDate;
        Amount = s.Amount;
        Currency = s.Currency.Trim().ToUpperInvariant();
        OrderNumber = s.OrderNumber.Trim().ToUpperInvariant();
        SupplyType = s.SupplyType;
        DeliveredOn = s.DeliveredOn;
        SubmissionSubject = $"{VendorName}_{InvoiceNumber}_{OrderNumber}";
    }

    /// <summary>Use when a vendor email is only known later (e.g. from the Business Central vendor card).</summary>
    public void SetVendorEmailIfMissing(string? email)
    {
        if (string.IsNullOrWhiteSpace(SenderEmail) && !string.IsNullOrWhiteSpace(email))
        {
            SenderEmail = email.Trim().ToLowerInvariant();
        }
    }

    /// <summary>People who cleared a blocking flag on this case.</summary>
    public IReadOnlyList<string> BlockingFlagResolverIds =>
        _flags.Where(f => f.Blocking && f.ResolvedById is not null).Select(f => f.ResolvedById!).Distinct().ToList();

    /// <summary>Accounts must be someone other than anyone who submitted (any round of an assisted entry) and anyone who cleared a blocking flag.</summary>
    public bool IsIndependentOfEarlierSteps(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.Id != RegisteredById
            && actor.Id != VerifiedById
            && !BlockingFlagResolverIds.Contains(actor.Id)
            && !_documents.Any(d => d.AddedById == actor.Id); // anyone who keyed in any round
    }

    private void EnsureIndependentOfEarlierSteps(Actor actor, string what)
    {
        if (!IsIndependentOfEarlierSteps(actor))
        {
            throw new DomainRuleException(RuleCodes.SegregationOfDuties, $"Someone who submitted or cleared a blocking flag on this case cannot also {what}.");
        }
    }

    private void EnsureNoBlockingFlags()
    {
        if (HasOpenBlockingFlags)
        {
            var codes = string.Join(", ", _flags.Where(f => f.IsOpen && f.Blocking).Select(f => f.Code));
            throw new DomainRuleException(RuleCodes.BlockingFlagOpen, $"Blocking flags must be cleared first: {codes}.");
        }
    }

    /// <summary>Every required slot filled and every current document scanned clean.</summary>
    private void EnsureCompleteAndClean()
    {
        var missing = MissingRequiredDocuments;
        if (missing.Count > 0)
        {
            throw new DomainRuleException(RuleCodes.MissingDocuments, "Missing documents: " + string.Join(", ", missing.Select(RequiredDocuments.Label)) + ".");
        }

        if (CurrentDocuments.Any(d => d.ScanStatus != ScanStatus.Clean))
        {
            throw new DomainRuleException(RuleCodes.BlockingFlagOpen, "Every document must pass the malware scan first.");
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
