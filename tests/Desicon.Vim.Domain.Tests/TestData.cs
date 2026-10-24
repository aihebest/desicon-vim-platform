using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Checklists;

namespace Desicon.Vim.Domain.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Monday10am = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero); // 10:00 WAT

    public static readonly Actor DocumentController = Actor.Create("dc-1", "Ada (legacy DC)", VimRole.DocumentController);
    public static readonly Actor Vendor = Actor.Create("vendor-v0001", "ABC Ltd (portal)", VimRole.Vendor) with { VendorNo = "V0001" };
    public static readonly Actor OtherVendor = Actor.Create("vendor-v0002", "XYZ Ltd (portal)", VimRole.Vendor) with { VendorNo = "V0002" };
    public static readonly Actor Procurement = Actor.Create("pr-1", "Halima (Procurement)", VimRole.Procurement);
    public static readonly Actor AccountsPayable = Actor.Create("ap-1", "Efe (AP)", VimRole.AccountsPayable);
    public static readonly Actor OtherAccountsPayable = Actor.Create("ap-2", "Gbenga (AP)", VimRole.AccountsPayable);
    public static readonly Actor FinanceManager = Actor.Create("fm-1", "Femi (FM)", VimRole.FinanceManager);

    public static ActionContext At(Actor actor, DateTimeOffset? when = null) => ActionContext.For(actor, when ?? Monday10am);

    public static StageDeadline Due(string holder = "Accounts Payable") => new(holder, Monday10am.AddHours(9), Monday10am.AddHours(4.5));

    public static InvoiceSubmission Submission(SupplyType supply = SupplyType.Materials, string invoiceNo = "INV-001", decimal amount = 1_250_000m) => new(
        VendorNo: "V0001",
        VendorName: "ABC Ltd",
        VendorEmail: "accounts@abc.example",
        InvoiceNumber: invoiceNo,
        InvoiceDate: new DateOnly(2026, 10, 14),
        Amount: amount,
        Currency: "NGN",
        OrderNumber: "PO123",
        SupplyType: supply,
        DeliveredOn: new DateOnly(2026, 10, 13));

    public static IReadOnlyCollection<ChecklistResponse> AllYes(SupplyType supply = SupplyType.Materials) =>
        DesiconInvoiceChecklist.Current.ItemsFor(ChecklistStage.AccountsCheck, supply)
            .Select(i => new ChecklistResponse(i.Code, ChecklistAnswer.Yes))
            .ToList();

    /// <summary>A portal submission with every required slot filled and scanned clean.</summary>
    public static InvoiceCase Submitted(SupplyType supply = SupplyType.Materials, Actor? by = null, SubmissionChannel channel = SubmissionChannel.Portal, bool scanClean = true)
    {
        var c = InvoiceCase.Submit("VIM-2026-000001", channel, Submission(supply), At(by ?? Vendor));
        foreach (var kind in RequiredDocuments.For(supply))
        {
            var doc = c.AttachDocument(kind, $"{kind}.pdf", "application/pdf", 1024, "ABCDEF" + kind, $"VIM-2026-000001/round-1/{kind}.pdf", At(by ?? Vendor));
            if (scanClean)
            {
                c.RecordScanResult(doc.Id, ScanStatus.Clean, At(Actor.System));
            }
        }

        return c;
    }

    public static InvoiceCase WithAccounts(Actor? submittedBy = null, SubmissionChannel channel = SubmissionChannel.Portal)
    {
        var c = Submitted(by: submittedBy, channel: channel);
        c.PassToAccounts(Due(), At(Actor.System));
        return c;
    }

    public static InvoiceCase Accepted()
    {
        var c = WithAccounts();
        c.Accept(AllYes(), DesiconInvoiceChecklist.Current, At(AccountsPayable));
        return c;
    }
}
