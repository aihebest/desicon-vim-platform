using System.Text;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Cases;
using Desicon.Vim.Application.Submission;
using Desicon.Vim.Application.VendorPortal;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.VendorAccess;
using Microsoft.Extensions.Logging.Abstractions;

namespace Desicon.Vim.Application.Tests;

/// <summary>Vendor portal phase 2: a vendor login only ever sees and changes its own vendor's invoices.</summary>
public class VendorPortalServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly Actor Procurement = Actor.Create("pr-1", "Halima (Procurement)", VimRole.Procurement);
    private static readonly Actor Ap = Actor.Create("ap-1", "Efe (AP)", VimRole.AccountsPayable);

    private readonly InMemoryStore _store = new();
    private readonly FakeErp _erp = new();
    private readonly FakeClock _clock = new(Now);

    public VendorPortalServiceTests()
    {
        _erp.Vendors["V0001"] = new ErpVendor("V0001", "ABC Ltd", false, ["accounts@abc.example"], []);
        _erp.Vendors["V0002"] = new ErpVendor("V0002", "XYZ Ltd", false, ["finance@xyz.example"], []);
    }

    private SubmissionService Submissions() =>
        new(_store, _store, _store, _erp, new AutomaticChecks(_store, _erp, Opts.Default()), _store, _store, _clock, Opts.Default(), NullLogger<SubmissionService>.Instance);

    private VendorPortalService Portal() => new(_store, Submissions(), _clock);

    private static VendorUser UserFor(string vendorNo, string vendorName, string email, string oid)
    {
        var ctx = ActionContext.For(Procurement, Now);
        var (invitation, _) = VendorInvitation.Create(vendorNo, vendorName, email, ContactConfirmation.VendorCardEmail, null, [email], TimeSpan.FromDays(7), ctx);
        invitation.Accept(oid, email, Now);
        return VendorUser.FromInvitation(invitation, oid, Now);
    }

    private static VendorInvoiceForm Form(string invoiceNo = "INV-001") =>
        new(invoiceNo, new DateOnly(2026, 10, 14), 1_000_000m, "ngn", "PO123", SupplyType.Materials, new DateOnly(2026, 10, 13));

    private static SubmittedFile Pdf(DocumentKind kind) =>
        new(kind, $"{kind}.pdf", "application/pdf", 21, _ => Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 test content"))));

    private static IReadOnlyList<SubmittedFile> AllSlots() => RequiredDocuments.For(SupplyType.Materials).Select(Pdf).ToList();

    [Fact]
    public async Task A_vendor_submits_for_its_own_vendor_only_and_is_acknowledged_at_its_login_email()
    {
        var abc = UserFor("V0001", "ABC Ltd", "accounts@abc.example", "tid:oid-1");

        var result = await Portal().SubmitAsync(abc, Form(), AllSlots(), default);

        Assert.True(result.Succeeded, string.Join(" ", result.Problems));
        var c = Assert.Single(_store.Cases);
        Assert.Equal("V0001", c.VendorNo);
        Assert.Equal(SubmissionChannel.Portal, c.Channel);
        Assert.Equal("NGN", c.Currency);
        Assert.Equal("accounts@abc.example", c.SenderEmail);
        Assert.Contains(_store.Sent, n => n.Template == "Acknowledgement" && n.ToAddress == "accounts@abc.example");
    }

    [Fact]
    public async Task Missing_document_slots_are_listed_and_nothing_is_recorded()
    {
        var abc = UserFor("V0001", "ABC Ltd", "accounts@abc.example", "tid:oid-1");

        var result = await Portal().SubmitAsync(abc, Form(), [Pdf(DocumentKind.Invoice)], default);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, p => p.Contains("Goods Received Voucher", StringComparison.Ordinal));
        Assert.Empty(_store.Cases);
    }

    [Fact]
    public async Task Another_vendors_invoice_is_invisible_and_cannot_be_resubmitted()
    {
        var abc = UserFor("V0001", "ABC Ltd", "accounts@abc.example", "tid:oid-1");
        var xyz = UserFor("V0002", "XYZ Ltd", "finance@xyz.example", "tid:oid-2");
        var submitted = await Portal().SubmitAsync(abc, Form(), AllSlots(), default);
        var id = submitted.CaseId!.Value;

        Assert.Null(await Portal().GetAsync(xyz, id, default));
        Assert.Empty(await Portal().ListAsync(xyz, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Portal().ResubmitAsync(xyz, id, Form(), [Pdf(DocumentKind.Invoice)], default));

        var own = await Portal().GetAsync(abc, id, default);
        Assert.NotNull(own);
        Assert.Single(await Portal().ListAsync(abc, default));
    }

    [Fact]
    public async Task A_returned_invoice_shows_the_reason_and_can_be_corrected_with_one_document()
    {
        var abc = UserFor("V0001", "ABC Ltd", "accounts@abc.example", "tid:oid-1");
        var id = (await Portal().SubmitAsync(abc, Form(), AllSlots(), default)).CaseId!.Value;
        var c = _store.Cases.Single(x => x.Id == id);
        foreach (var d in c.Documents)
        {
            c.RecordScanResult(d.Id, ScanStatus.Clean, ActionContext.For(Actor.System, Now));
        }

        c.PassToAccounts(Opts.Default().Value.Deadlines.Create("Accounts Payable", Now, Opts.Default().Value.AccountsCheckWorkingDays), ActionContext.For(Actor.System, Now));
        c.ReturnToVendor("The GRV is not signed.", ActionContext.For(Ap, Now));

        var detail = await Portal().GetAsync(abc, id, default);
        Assert.NotNull(detail);
        Assert.True(detail.CanResubmit);
        Assert.Equal("The GRV is not signed.", detail.ReturnReason);

        var result = await Portal().ResubmitAsync(abc, id, Form(), [Pdf(DocumentKind.GoodsReceivedVoucher)], default);
        Assert.True(result.Succeeded, string.Join(" ", result.Problems));
        Assert.Equal(CaseStatus.Received, c.Status);
        Assert.Equal(2, c.SubmissionRound);
    }

    [Fact]
    public async Task A_double_submit_returns_the_first_case_instead_of_creating_a_duplicate()
    {
        var abc = UserFor("V0001", "ABC Ltd", "accounts@abc.example", "tid:oid-1");

        var first = await Portal().SubmitAsync(abc, Form(), AllSlots(), default);
        var again = await Portal().SubmitAsync(abc, Form(), AllSlots(), default);

        Assert.True(again.Succeeded);
        Assert.Equal(first.CaseId, again.CaseId);
        Assert.Single(_store.Cases);
    }
}
