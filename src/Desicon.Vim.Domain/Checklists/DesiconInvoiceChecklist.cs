namespace Desicon.Vim.Domain.Checklists;

/// <summary>
/// The Desicon Engineering Nigeria Limited "Invoice Review Checklist" and the
/// verification points of SOP DEL/FIN/AP/SOP/001 section 7.4, as version 1.
///
/// Wording is Finance's. Two items reflect open questions in the design basis
/// (section 13.3) and are marked below; change them here once Finance decides.
/// </summary>
public static class DesiconInvoiceChecklist
{
    public const string CurrentVersion = "FIN-CHK-1";

    private const string Prep = "Invoice preparation";
    private const string Address = "Invoice address";
    private const string Support = "Supporting documents";
    private const string Integrity = "Document integrity";
    private const string Review = "Responsible Officer review (SOP 7.4)";

    public static ChecklistDefinition Version1 { get; } = new(CurrentVersion,
    [
        // ---- Intake: Document Controller ----
        new("PREP_LETTERHEAD", ChecklistStage.Intake, Prep, "Invoice is on the vendor's letter-headed paper", AppliesTo.All),
        new("PREP_INVOICE_NO", ChecklistStage.Intake, Prep, "Invoice contains a unique invoice number", AppliesTo.All),
        new("PREP_PO_REF", ChecklistStage.Intake, Prep, "Purchase Order / Service Order number is stated on the invoice", AppliesTo.All),
        new("PREP_DESCRIPTION", ChecklistStage.Intake, Prep, "Clear description of items delivered or service rendered", AppliesTo.All),
        new("PREP_STANDARD", ChecklistStage.Intake, Prep, "Standard invoice, not a proforma invoice", AppliesTo.All),
        new("PREP_DATE", ChecklistStage.Intake, Prep, "Invoice is dated", AppliesTo.All),
        new("PREP_AMOUNT_WORDS", ChecklistStage.Intake, Prep, "Invoice amount is stated in words and figures", AppliesTo.All),
        new("PREP_VAT", ChecklistStage.Intake, Prep, "VAT is shown separately on the invoice", AppliesTo.All, AllowNotApplicable: true),
        new("PREP_TAX_ID", ChecklistStage.Intake, Prep, "Vendor's new Tax ID is shown", AppliesTo.All),
        new("PREP_BANK", ChecklistStage.Intake, Prep, "Bank details include account name, account number, banker's name and sort code", AppliesTo.All),
        new("PREP_BANK_CURRENCY", ChecklistStage.Intake, Prep, "USD bank details on USD-payable invoices; NGN bank details on NGN-payable invoices", AppliesTo.All),
        new("PREP_CURRENCY", ChecklistStage.Intake, Prep, "Invoice currency matches the currency of the PO / SO", AppliesTo.All),
        new("ADDR_BILL_TO", ChecklistStage.Intake, Address, "Addressed to Accounts Payable, Desicon Engineering Limited, 60 King Perekule Street, GRA Phase 2, Port Harcourt, Rivers State", AppliesTo.All),

        new("DOC_INVOICE", ChecklistStage.Intake, Support, "Invoice", AppliesTo.All),
        new("DOC_WAYBILL", ChecklistStage.Intake, Support, "Waybill / packing list / delivery note", AppliesTo.MaterialsOnly),
        new("DOC_GRV", ChecklistStage.Intake, Support, "Goods Received Voucher (GRV), duly signed", AppliesTo.All),
        new("DOC_PO", ChecklistStage.Intake, Support, "Valid Purchase Order", AppliesTo.MaterialsOnly),
        new("DOC_WCC", ChecklistStage.Intake, Support, "Work Completion Certificate (WCC), duly signed", AppliesTo.ServicesOnly),
        new("DOC_TIMESHEETS", ChecklistStage.Intake, Support, "Approved personnel or equipment timesheets (where applicable)", AppliesTo.ServicesOnly, AllowNotApplicable: true),
        new("DOC_THIRD_PARTY", ChecklistStage.Intake, Support, "Third-party invoice (where applicable)", AppliesTo.ServicesOnly, AllowNotApplicable: true),
        new("DOC_SO", ChecklistStage.Intake, Support, "Valid Service Order", AppliesTo.ServicesOnly),
        new("INTEGRITY_LEGIBLE", ChecklistStage.Intake, Integrity, "All documents are clear, legible and free from blurring and mutilation", AppliesTo.All),

        // ---- Verification: Responsible Officer ----
        new("REV_CORRECT_ORDER", ChecklistStage.Verification, Review, "Invoice is against the correct PO / SO", AppliesTo.All),
        new("REV_RATES", ChecklistStage.Verification, Review, "Rates agree with the agreed PO / SO rates", AppliesTo.All),
        new("REV_QUANTITY", ChecklistStage.Verification, Review, "Quantities agree with the GRV / waybill or WCC / timesheets", AppliesTo.All),
        new("REV_SCOPE", ChecklistStage.Verification, Review, "Work executed within the scope and description of the PO / SO", AppliesTo.All),

        // Open question (design basis 13.3): Finance's checklist says "aligns with PO amount".
        // Worded here as "does not exceed remaining balance" so partial and milestone invoices pass.
        new("REV_AMOUNT_WITHIN_ORDER", ChecklistStage.Verification, Review, "Invoice amount does not exceed the remaining PO / SO balance", AppliesTo.All),

        new("REV_RECEIVER_SIGNED", ChecklistStage.Verification, Review, "Delivery note / GRV / WCC signed by an authorised Desicon receiver", AppliesTo.All),
        new("REV_AUTHENTIC", ChecklistStage.Verification, Review, "Documents appear authentic and consistent with each other", AppliesTo.All),
    ]);
}
