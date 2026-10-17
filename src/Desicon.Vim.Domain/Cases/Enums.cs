namespace Desicon.Vim.Domain.Cases;

/// <summary>
/// Where a case is in its life. Mirrors section 4.3 of the design basis
/// (DEL/ICT/ARC/VIM/001) and SOP DEL/FIN/AP/SOP/001.
/// </summary>
public enum CaseStatus
{
    /// <summary>Package captured (email, physical or portal); acknowledgement sent.</summary>
    Received = 0,

    /// <summary>Document Controller confirmed details, category and intake checklist.</summary>
    Registered = 1,

    /// <summary>Incomplete or incorrect; reason recorded; waiting for the vendor.</summary>
    ReturnedToVendor = 2,

    /// <summary>With the Responsible Officer for verification.</summary>
    UnderVerification = 3,

    /// <summary>Responsible Officer confirmed against the checklist; ready for AP.</summary>
    Verified = 4,

    /// <summary>AP Coordinator performing the final financial check.</summary>
    WithAccountsPayable = 5,

    /// <summary>Purchase invoice exists in Business Central; document number recorded.</summary>
    EnteredInErp = 6,

    /// <summary>Payment confirmed from Business Central.</summary>
    Paid = 7,

    /// <summary>Finished. No further actions.</summary>
    Closed = 8,

    /// <summary>Withdrawn, duplicate, or superseded by a resubmission.</summary>
    Cancelled = 9,
}

/// <summary>How the package reached Desicon.</summary>
public enum SubmissionChannel
{
    Email = 0,
    Physical = 1,
    Portal = 2,
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
