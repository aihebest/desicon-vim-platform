namespace Desicon.Vim.Domain.Cases;

/// <summary>
/// Where a case is in its life. Mirrors section 4.3 of the design basis
/// (DEL/ICT/ARC/VIM/001) and SOP DEL/FIN/AP/SOP/001.
/// </summary>
public enum CaseStatus
{
    /// <summary>Submitted (portal or assisted); automatic checks are running (malware scan, Business Central, duplicates).</summary>
    Received = 0,

    /// <summary>Legacy (workflow v1): Document Controller registration. No longer used for new cases.</summary>
    Registered = 1,

    /// <summary>Rejected with reasons (automatically, by Accounts or by the Finance Manager); waiting for the vendor to resubmit.</summary>
    ReturnedToVendor = 2,

    /// <summary>Legacy (workflow v1): with the Responsible Officer. Department verification now happens before submission (GRV / TRV in Business Central).</summary>
    UnderVerification = 3,

    /// <summary>Legacy (workflow v1): verified by the Responsible Officer.</summary>
    Verified = 4,

    /// <summary>Passed the automatic checks; waiting for Accounts' confirmatory check.</summary>
    WithAccountsPayable = 5,

    /// <summary>Purchase invoice exists in Business Central; document number recorded.</summary>
    EnteredInErp = 6,

    /// <summary>Payment confirmed from Business Central.</summary>
    Paid = 7,

    /// <summary>Finished. No further actions.</summary>
    Closed = 8,

    /// <summary>Withdrawn, duplicate, or raised in error.</summary>
    Cancelled = 9,

    /// <summary>An automatic check needs a decision (e.g. possible duplicate); with the Finance Manager.</summary>
    ExceptionReview = 10,

    /// <summary>
    /// Accounts confirmed the submission. This is the official submission date:
    /// the vendor is told, and the credit period counts from <see cref="InvoiceCase.AcceptedAtUtc"/>.
    /// </summary>
    Accepted = 11,
}

/// <summary>How the package reached Desicon.</summary>
public enum SubmissionChannel
{
    /// <summary>Legacy: read from the invoice mailbox (switched off; vendors submit on the portal).</summary>
    Email = 0,

    /// <summary>Legacy: paper package scanned by the Document Controller.</summary>
    Physical = 1,

    /// <summary>Submitted by the vendor on the vendor portal.</summary>
    Portal = 2,

    /// <summary>Keyed in by Accounts Payable on the vendor's behalf (vendors who cannot use the portal).</summary>
    Assisted = 3,
}

/// <summary>Materials delivered (PO) or service rendered (SO). Drives the required documents.</summary>
public enum SupplyType
{
    Unknown = 0,
    Materials = 1,
    Services = 2,
}

/// <summary>Application roles. Names match the Entra app roles (prefixed "Vim.").</summary>
public enum VimRole
{
    DocumentController = 0,
    Reviewer = 1,
    AccountsPayable = 2,
    FinanceManager = 3,
    PlatformAdmin = 4,
    Auditor = 5,

    /// <summary>A vendor signed in to the portal (External ID). Not an Entra app role in the staff tenant.</summary>
    Vendor = 6,
    System = 99,
}

/// <summary>Kinds of file attached to a case.</summary>
public enum DocumentKind
{
    SubmissionPackage = 0,
    Invoice = 1,
    WaybillOrDeliveryNote = 2,
    GoodsReceivedVoucher = 3,
    PurchaseOrder = 4,
    ServiceOrder = 5,
    WorkCompletionCertificate = 6,
    Timesheet = 7,
    ThirdPartyInvoice = 8,
    Correspondence = 9,
    Other = 99,
}
