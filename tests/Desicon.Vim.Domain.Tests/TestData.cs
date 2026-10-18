using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Checklists;

namespace Desicon.Vim.Domain.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Monday10am = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero); // 10:00 WAT

    public static readonly Actor DocumentController = Actor.Create("dc-1", "Ada (DC)", VimRole.DocumentController);
    public static readonly Actor OtherDocumentController = Actor.Create("dc-2", "Bola (DC delegate)", VimRole.DocumentController);
    public static readonly Actor Reviewer = Actor.Create("ro-1", "Chidi (GMC Cost Controller)", VimRole.Reviewer);
    public static readonly Actor DelegateReviewer = Actor.Create("ro-2", "Dayo (delegate)", VimRole.Reviewer);
    public static readonly Actor OtherReviewer = Actor.Create("ro-9", "Uche (other RO)", VimRole.Reviewer);
    public static readonly Actor AccountsPayable = Actor.Create("ap-1", "Efe (AP)", VimRole.AccountsPayable);
    public static readonly Actor FinanceManager = Actor.Create("fm-1", "Femi (FM)", VimRole.FinanceManager);

    public static ActionContext At(Actor actor, DateTimeOffset? when = null) => ActionContext.For(actor, when ?? Monday10am);

    public static InvoiceDetails Details(SupplyType supply = SupplyType.Materials) => new(
        VendorName: "ABC Ltd",
        VendorNo: "V0001",
        InvoiceNumber: "INV-001",
        InvoiceDate: new DateOnly(2026, 10, 14),
        Amount: 1_250_000m,
        Currency: "NGN",
        OrderNumber: "PO123",
        SupplyType: supply,
        CategoryCode: "GMC");

    public static IReadOnlyCollection<ChecklistResponse> AllYes(ChecklistStage stage, SupplyType supply = SupplyType.Materials) =>
        DesiconInvoiceChecklist.Version1.ItemsFor(stage, supply)
            .Select(i => new ChecklistResponse(i.Code, ChecklistAnswer.Yes))
            .ToList();

    public static ReviewerAssignment Assignment(DateTimeOffset? now = null) => new(
        Reviewer.Id, Reviewer.DisplayName, [DelegateReviewer.Id], (now ?? Monday10am).AddHours(48), (now ?? Monday10am).AddHours(24));

    public static InvoiceCase NewCase()
    {
        var c = InvoiceCase.Receive("VIM-2026-000001", SubmissionChannel.Email, "accounts@abc.example", "ABC Ltd", "ABC LTD_INV001_PO123_14Oct26", At(Actor.System));
        var doc = c.AttachDocument(DocumentKind.SubmissionPackage, "invoice.pdf", "application/pdf", 1024, "ABCDEF", "VIM-2026-000001/round-1/x.pdf", At(Actor.System));
        c.RecordScanResult(doc.Id, ScanStatus.Clean, At(Actor.System));
        return c;
    }

    public static InvoiceCase Registered()
    {
        var c = NewCase();
        c.Register(Details(), AllYes(ChecklistStage.Intake), DesiconInvoiceChecklist.Version1, At(DocumentController));
        return c;
    }

    public static InvoiceCase UnderVerification()
    {
        var c = Registered();
        c.SendForVerification(Assignment(), At(DocumentController));
        return c;
    }

    public static InvoiceCase Verified()
    {
        var c = UnderVerification();
        c.Verify(AllYes(ChecklistStage.Verification), DesiconInvoiceChecklist.Version1, At(Reviewer));
        return c;
    }
}
