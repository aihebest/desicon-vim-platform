using Desicon.Vim.Domain.Checklists;

namespace Desicon.Vim.Domain.Cases;

/// <summary>Who did it, when, and the correlation id that ties logs together.</summary>
public sealed record ActionContext(Actor Actor, DateTimeOffset NowUtc, string CorrelationId)
{
    public static ActionContext For(Actor actor, DateTimeOffset nowUtc) =>
        new(actor, nowUtc, Guid.NewGuid().ToString("N"));
}

/// <summary>
/// What the vendor (on the portal) or Accounts Payable (assisted submission) enters
/// with the documents. Department verification has already happened: the GRV / TRV
/// is posted in Business Central before the vendor can invoice.
/// </summary>
public sealed record InvoiceSubmission(
    string VendorNo,
    string VendorName,
    string? VendorEmail,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    decimal Amount,
    string Currency,
    string OrderNumber,
    SupplyType SupplyType,
    DateOnly DeliveredOn)
{
    public IReadOnlyList<string> Problems(DateOnly today)
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(VendorNo))
        {
            p.Add("The Business Central vendor number is required.");
        }

        if (string.IsNullOrWhiteSpace(VendorName))
        {
            p.Add("Vendor name is required.");
        }

        if (string.IsNullOrWhiteSpace(InvoiceNumber))
        {
            p.Add("Invoice number is required.");
        }

        if (InvoiceDate == default || InvoiceDate > today)
        {
            p.Add("Invoice date is required and cannot be in the future.");
        }

        if (Amount <= 0)
        {
            p.Add("Amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(Currency) || Currency.Trim().Length != 3)
        {
            p.Add("Currency must be a 3-letter code such as NGN or USD.");
        }

        if (string.IsNullOrWhiteSpace(OrderNumber))
        {
            p.Add("PO / SO number is required.");
        }

        if (SupplyType == SupplyType.Unknown)
        {
            p.Add("Select materials (PO) or services (SO).");
        }

        if (DeliveredOn == default || DeliveredOn > today)
        {
            p.Add("Delivery / completion date is required and cannot be in the future.");
        }

        return p;
    }
}

/// <summary>
/// The document slots a submission must fill (Augustine's point at the stakeholder
/// meeting): the system refuses a submission with a required slot empty.
/// </summary>
public static class RequiredDocuments
{
    private static readonly DocumentKind[] Materials =
        [DocumentKind.Invoice, DocumentKind.WaybillOrDeliveryNote, DocumentKind.GoodsReceivedVoucher, DocumentKind.PurchaseOrder];

    private static readonly DocumentKind[] Services =
        [DocumentKind.Invoice, DocumentKind.WorkCompletionCertificate, DocumentKind.ServiceOrder];

    private static readonly DocumentKind[] OptionalKinds =
        [DocumentKind.Timesheet, DocumentKind.ThirdPartyInvoice, DocumentKind.Other];

    public static IReadOnlyList<DocumentKind> For(SupplyType supply) => supply switch
    {
        SupplyType.Materials => Materials,
        SupplyType.Services => Services,
        _ => [DocumentKind.Invoice],
    };

    public static IReadOnlyList<DocumentKind> Optional(SupplyType supply) =>
        supply == SupplyType.Services ? OptionalKinds : [DocumentKind.Other];

    public static string Label(DocumentKind kind) => kind switch
    {
        DocumentKind.Invoice => "Invoice",
        DocumentKind.WaybillOrDeliveryNote => "Waybill / delivery note",
        DocumentKind.GoodsReceivedVoucher => "Goods Received Voucher (GRV), signed",
        DocumentKind.PurchaseOrder => "Purchase Order",
        DocumentKind.ServiceOrder => "Service Order",
        DocumentKind.WorkCompletionCertificate => "Work Completion Certificate / TRV, signed",
        DocumentKind.Timesheet => "Approved timesheets (if applicable)",
        DocumentKind.ThirdPartyInvoice => "Third-party invoice (if applicable)",
        DocumentKind.Other => "Other supporting document",
        _ => kind.ToString(),
    };
}

/// <summary>When the current holder of a case must act, and when to remind them.</summary>
public sealed record StageDeadline(string HolderName, DateTimeOffset DueAtUtc, DateTimeOffset ReminderAtUtc);

/// <summary>Fields read from the invoice PDF by Document Intelligence. Suggestions only.</summary>
public sealed record ExtractedInvoiceFields(
    string? VendorName,
    string? InvoiceNumber,
    DateOnly? InvoiceDate,
    decimal? Amount,
    string? Currency,
    string? OrderNumber,
    string? BankAccountNumber,
    double? Confidence);

public enum ScanStatus
{
    Pending = 0,
    Clean = 1,
    Malicious = 2,
    ScanFailed = 3,
}

/// <summary>A file stored in Blob Storage and attached to a case.</summary>
public sealed class CaseDocument
{
    private CaseDocument()
    {
    }

    public Guid Id { get; private set; }

    public Guid CaseId { get; private set; }

    public DocumentKind Kind { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 of the file content, so any later change to the stored file is detectable.</summary>
    public string Sha256 { get; private set; } = string.Empty;

    public string BlobPath { get; private set; } = string.Empty;

    /// <summary>1 for the first submission, 2 after the first resubmission, and so on.</summary>
    public int SubmissionRound { get; private set; }

    public DateTimeOffset AddedAtUtc { get; private set; }

    public string AddedById { get; private set; } = string.Empty;

    public ScanStatus ScanStatus { get; private set; }

    internal static CaseDocument Create(Guid caseId, DocumentKind kind, string fileName, string contentType, long size, string sha256, string blobPath, int round, ActionContext ctx) => new()
    {
        Id = Guid.CreateVersion7(ctx.NowUtc),
        CaseId = caseId,
        Kind = kind,
        FileName = fileName,
        ContentType = contentType,
        SizeBytes = size,
        Sha256 = sha256,
        BlobPath = blobPath,
        SubmissionRound = round,
        AddedAtUtc = ctx.NowUtc,
        AddedById = ctx.Actor.Id,
        ScanStatus = ScanStatus.Pending,
    };

    internal void SetScanStatus(ScanStatus status) => ScanStatus = status;
}

/// <summary>A recorded checklist answer, kept for every stage and round.</summary>
public sealed class CaseChecklistAnswer
{
    private CaseChecklistAnswer()
    {
    }

    public Guid Id { get; private set; }

    public Guid CaseId { get; private set; }

    public string ChecklistVersion { get; private set; } = string.Empty;

    public ChecklistStage Stage { get; private set; }

    public string ItemCode { get; private set; } = string.Empty;

    public ChecklistAnswer Answer { get; private set; }

    public string? Remark { get; private set; }

    public string AnsweredById { get; private set; } = string.Empty;

    public DateTimeOffset AnsweredAtUtc { get; private set; }

    internal static CaseChecklistAnswer Create(Guid caseId, string version, ChecklistStage stage, ChecklistResponse r, ActionContext ctx) => new()
    {
        Id = Guid.CreateVersion7(ctx.NowUtc),
        CaseId = caseId,
        ChecklistVersion = version,
        Stage = stage,
        ItemCode = r.ItemCode,
        Answer = r.Answer,
        Remark = r.Remark,
        AnsweredById = ctx.Actor.Id,
        AnsweredAtUtc = ctx.NowUtc,
    };
}

/// <summary>Standard flag codes. Blocking flags must be cleared before AP can accept the case.</summary>
public static class FlagCodes
{
    public const string PossibleDuplicate = "POSSIBLE_DUPLICATE";
    public const string BankDetailsMismatch = "BANK_DETAILS_MISMATCH";
    public const string UnknownSender = "UNKNOWN_SENDER";
    public const string SenderAuthenticationFailed = "SENDER_AUTH_FAILED";
    public const string ErpValidationFailed = "ERP_VALIDATION_FAILED";
    public const string AmountExceedsOrderBalance = "AMOUNT_EXCEEDS_ORDER_BALANCE";
    public const string MaliciousAttachment = "MALICIOUS_ATTACHMENT";
    public const string SenderAuthenticationUnknown = "SENDER_AUTH_UNKNOWN";
    public const string BankDetailsUnverified = "BANK_DETAILS_UNVERIFIED";
    public const string ErpUnavailable = "ERP_UNAVAILABLE";

    /// <summary>
    /// Rule: every BLOCKING flag can be cleared only by the Finance Manager, and
    /// whoever clears it may not then accept that case in Accounts. Non-blocking
    /// (advisory) flags can be cleared by Accounts or the Finance Manager.
    /// </summary>
    public static bool RequiresFinanceManager(CaseFlag flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        return flag.Blocking;
    }
}

/// <summary>A warning raised on a case (by a person or by an automated check).</summary>
public sealed class CaseFlag
{
    private CaseFlag()
    {
    }

    public Guid Id { get; private set; }

    public Guid CaseId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public bool Blocking { get; private set; }

    public DateTimeOffset RaisedAtUtc { get; private set; }

    public string RaisedById { get; private set; } = string.Empty;

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public string? ResolvedById { get; private set; }

    public string? Resolution { get; private set; }

    public bool IsOpen => ResolvedAtUtc is null;

    internal static CaseFlag Create(Guid caseId, string code, string description, bool blocking, ActionContext ctx) => new()
    {
        Id = Guid.CreateVersion7(ctx.NowUtc),
        CaseId = caseId,
        Code = code,
        Description = description,
        Blocking = blocking,
        RaisedAtUtc = ctx.NowUtc,
        RaisedById = ctx.Actor.Id,
    };

    internal void Resolve(string resolution, ActionContext ctx)
    {
        ResolvedAtUtc = ctx.NowUtc;
        ResolvedById = ctx.Actor.Id;
        Resolution = resolution;
    }
}
