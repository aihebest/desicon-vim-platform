using System.Text;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Cases;
using Desicon.Vim.Application.Intake;
using Desicon.Vim.Application.Sla;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Checklists;
using Desicon.Vim.Domain.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace Desicon.Vim.Application.Tests;

public class ApplicationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly Actor Dc = Actor.Create("dc-1", "Ada (DC)", VimRole.DocumentController);
    private static readonly Actor Ro = Actor.Create("ro-1", "Chidi (RO)", VimRole.Reviewer);

    private readonly InMemoryStore _store = new();
    private readonly FakeErp _erp = new();
    private readonly FakeClock _clock = new(Now);

    public ApplicationServiceTests()
    {
        _store.Routes.Add(new CategoryRoute("GMC", "GMC invoices", Ro.Id, Ro.DisplayName));
        _erp.Vendors["V0001"] = new ErpVendor("V0001", "ABC Ltd", false, ["accounts@abc.example"], ["0123456789"]);
    }

    private IntakeService Intake(ExtractedInvoiceFields? extracted = null) => new(
        _store, _store, _store, _store, new FakeExtractor(extracted), _erp, _store, _store, _clock, Opts.Default(), NullLogger<IntakeService>.Instance);

    private CaseWorkflowService Workflow() => new(_store, _store, _store, _store, _erp, _store, _store, _clock, Opts.Default());

    private static InboundAttachment Pdf(string name = "invoice.pdf") =>
        new(name, "application/pdf", 20, _ => Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 test content"))));

    private static InboundEmail Email(string id, string subject = "ABC LTD_INV001_PO123_14Oct26", bool? auth = true, params InboundAttachment[] files) =>
        EmailFrom("accounts@abc.example", id, subject, auth, files);

    private static InboundEmail EmailFrom(string from, string id, string subject = "ABC LTD_INV001_PO123_14Oct26", bool? auth = true, params InboundAttachment[] files) =>
        new(id, from, "ABC Ltd", subject, Now, auth, files.Length == 0 ? [Pdf()] : files);

    private static InvoiceDetails Details(string invoiceNo = "INV-001", decimal amount = 1_000_000m) =>
        new("ABC Ltd", "V0001", invoiceNo, new DateOnly(2026, 10, 14), amount, "NGN", "PO123", SupplyType.Materials, "GMC");

    private static IReadOnlyCollection<ChecklistResponse> AllYes(ChecklistStage stage) =>
        DesiconInvoiceChecklist.Version1.ItemsFor(stage, SupplyType.Materials).Select(i => new ChecklistResponse(i.Code, ChecklistAnswer.Yes)).ToList();

    [Fact]
    public async Task Email_with_pdf_creates_a_case_and_acknowledges_the_vendor()
    {
        var result = await Intake().ProcessEmailAsync(Email("m1"), default);

        Assert.Equal(IntakeOutcome.CreatedCase, result.Outcome);
        var c = Assert.Single(_store.Cases);
        Assert.Equal("VIM-2026-000001", c.Reference);
        Assert.Single(c.Documents);
        Assert.Single(_store.Sent, n => n.Template == "Acknowledgement" && n.ToAddress == "accounts@abc.example");
    }

    [Fact]
    public async Task Processing_the_same_email_twice_creates_only_one_case()
    {
        await Intake().ProcessEmailAsync(Email("m1"), default);
        var second = await Intake().ProcessEmailAsync(Email("m1"), default);

        Assert.Equal(IntakeOutcome.AlreadyProcessed, second.Outcome);
        Assert.Single(_store.Cases);
    }

    [Fact]
    public async Task Email_without_a_pdf_is_rejected_with_guidance()
    {
        var photo = new InboundAttachment("invoice.jpg", "image/jpeg", 100, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        var result = await Intake().ProcessEmailAsync(Email("m2", files: photo), default);

        Assert.Equal(IntakeOutcome.RejectedNoPdf, result.Outcome);
        Assert.Empty(_store.Cases);
        Assert.Single(_store.Sent, n => n.Template == "NonPdfRejected");
    }

    [Fact]
    public async Task A_file_named_pdf_that_is_not_a_pdf_is_rejected()
    {
        var fake = new InboundAttachment("invoice.pdf", "application/pdf", 100, _ => Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("MZ executable"))));
        var result = await Intake().ProcessEmailAsync(Email("m3", files: fake), default);
        Assert.Equal(IntakeOutcome.RejectedNoPdf, result.Outcome);
    }

    [Fact]
    public async Task Failed_sender_authentication_raises_a_blocking_flag()
    {
        await Intake().ProcessEmailAsync(Email("m4", auth: false), default);
        var flag = Assert.Single(_store.Cases[0].Flags);
        Assert.Equal(FlagCodes.SenderAuthenticationFailed, flag.Code);
        Assert.True(flag.Blocking);
    }

    [Fact]
    public async Task Reply_quoting_the_reference_is_added_to_the_returned_case()
    {
        await Intake().ProcessEmailAsync(Email("m1"), default);
        var c = _store.Cases[0];
        await Workflow().ReturnToVendorAsync(c.Id, "GRV is missing from the package.", Dc, default);

        var result = await Intake().ProcessEmailAsync(Email("m5", subject: $"RE: {c.Reference} corrected GRV attached"), default);

        Assert.Equal(IntakeOutcome.AddedToResubmission, result.Outcome);
        Assert.Single(_store.Cases);
        Assert.Equal(CaseStatus.Received, c.Status);
        Assert.Equal(2, c.SubmissionRound);
        Assert.Equal(2, c.Documents.Count);
    }

    [Fact]
    public async Task Registering_a_duplicate_invoice_raises_a_blocking_flag()
    {
        await Intake().ProcessEmailAsync(Email("m1"), default);
        await Intake().ProcessEmailAsync(Email("m2"), default);
        var wf = Workflow();
        await wf.RegisterAsync(_store.Cases[0].Id, Details("INV-001"), AllYes(ChecklistStage.Intake), Dc, default);
        await wf.RegisterAsync(_store.Cases[1].Id, Details("inv 1"), AllYes(ChecklistStage.Intake), Dc, default);

        Assert.Contains(_store.Cases[1].Flags, f => f.Code == FlagCodes.PossibleDuplicate && f.Blocking);
        Assert.DoesNotContain(_store.Cases[0].Flags, f => f.Code == FlagCodes.PossibleDuplicate);
    }

    [Fact]
    public async Task Amount_above_the_remaining_order_balance_is_flagged()
    {
        _erp.Order = _erp.Order with { RemainingAmount = 500_000m };
        await Intake().ProcessEmailAsync(Email("m1"), default);
        await Workflow().RegisterAsync(_store.Cases[0].Id, Details(amount: 750_000m), AllYes(ChecklistStage.Intake), Dc, default);

        Assert.Contains(_store.Cases[0].Flags, f => f.Code == FlagCodes.AmountExceedsOrderBalance);
    }

    [Fact]
    public async Task Bank_account_that_differs_from_the_vendor_master_is_flagged()
    {
        var extracted = new ExtractedInvoiceFields("ABC Ltd", "INV-001", null, 1_000_000m, "NGN", "PO123", "9999999999", 0.9);
        await Intake(extracted).ProcessEmailAsync(Email("m1"), default);
        await Workflow().RegisterAsync(_store.Cases[0].Id, Details(), AllYes(ChecklistStage.Intake), Dc, default);

        var flag = Assert.Single(_store.Cases[0].Flags, f => f.Code == FlagCodes.BankDetailsMismatch);
        Assert.True(flag.Blocking);
    }

    [Fact]
    public async Task Sending_for_verification_assigns_the_category_reviewer_with_a_48_hour_deadline()
    {
        await Intake().ProcessEmailAsync(Email("m1"), default);
        var wf = Workflow();
        var id = _store.Cases[0].Id;
        await wf.RegisterAsync(id, Details(), AllYes(ChecklistStage.Intake), Dc, default);
        await wf.SendForVerificationAsync(id, Dc, default);

        var c = _store.Cases[0];
        Assert.Equal(CaseStatus.UnderVerification, c.Status);
        Assert.Equal(Ro.Id, c.AssignedReviewerId);
        Assert.Equal(Now.AddHours(48), c.DueAtUtc);
        Assert.Single(_store.Sent, n => n.Template == "AssignedToReviewer" && n.ToAddress == Ro.Id);
    }

    [Fact]
    public async Task Sla_monitor_reminds_halfway_then_escalates_after_the_deadline()
    {
        await Intake().ProcessEmailAsync(Email("m1"), default);
        var wf = Workflow();
        var id = _store.Cases[0].Id;
        await wf.RegisterAsync(id, Details(), AllYes(ChecklistStage.Intake), Dc, default);
        await wf.SendForVerificationAsync(id, Dc, default);
        var monitor = new SlaMonitor(_store, _store, _store, _clock, Opts.Default(), NullLogger<SlaMonitor>.Instance);

        _clock.UtcNow = Now.AddHours(25);
        Assert.Equal(new SlaRunResult(1, 0), await monitor.RunAsync(default));
        Assert.Equal(new SlaRunResult(0, 0), await monitor.RunAsync(default));

        _clock.UtcNow = Now.AddHours(49);
        Assert.Equal(new SlaRunResult(0, 1), await monitor.RunAsync(default));
        Assert.Single(_store.Sent, n => n.Template == "Escalation" && n.ToAddress == "finance.manager@desicon.example");
    }

    [Fact]
    public async Task Registering_to_an_unknown_category_is_refused()
    {
        await Intake().ProcessEmailAsync(Email("m1"), default);
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Workflow().RegisterAsync(_store.Cases[0].Id, Details() with { CategoryCode = "NOPE" }, AllYes(ChecklistStage.Intake), Dc, default));
        Assert.Equal(RuleCodes.InvalidValue, ex.Code);
    }

    [Fact]
    public async Task Physical_submission_is_logged_with_a_reference_and_stored_scan()
    {
        var file = new CaseWorkflowService.ScannedFile("scan.pdf", "application/pdf", _ => Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 scan"))));
        var c = await Workflow().ReceivePhysicalAsync(Dc, "accounts@abc.example", "ABC Ltd", [file], default);

        Assert.Equal(SubmissionChannel.Physical, c.Channel);
        Assert.Equal("VIM-2026-000001", c.Reference);
        Assert.Single(c.Documents);
        Assert.Single(_store.Blobs);
    }

    [Fact]
    public async Task Vendor_text_in_notifications_is_html_encoded()
    {
        await Intake().ProcessEmailAsync(Email("m1", subject: "<script>alert(1)</script>"), default);
        await Workflow().ReturnToVendorAsync(_store.Cases[0].Id, "Bad <b>doc</b> attached", Dc, default);
        var returned = Assert.Single(_store.Sent, n => n.Template == "ReturnedToVendor");
        Assert.Contains("Bad &lt;b&gt;doc&lt;/b&gt; attached", returned.HtmlBody);
    }

    [Fact]
    public async Task A_stranger_quoting_a_reference_cannot_add_documents_to_that_case()
    {
        await Intake().ProcessEmailAsync(Email("m1"), default);
        var c = _store.Cases[0];
        await Workflow().ReturnToVendorAsync(c.Id, "GRV is missing from the package.", Dc, default);

        var result = await Intake().ProcessEmailAsync(EmailFrom("attacker@evil.example", "m9", $"RE: {c.Reference} corrected"), default);

        Assert.Equal(IntakeOutcome.CreatedCase, result.Outcome);
        Assert.Equal(CaseStatus.ReturnedToVendor, c.Status);
        Assert.Single(c.Documents);
        Assert.Contains(_store.Cases[1].Flags, f => f.Code == FlagCodes.UnknownSender);
    }

    [Fact]
    public async Task Missing_authentication_result_raises_an_advisory_flag()
    {
        await Intake().ProcessEmailAsync(Email("m1", auth: null), default);
        var flag = Assert.Single(_store.Cases[0].Flags, f => f.Code == FlagCodes.SenderAuthenticationUnknown);
        Assert.False(flag.Blocking);
    }

    [Fact]
    public async Task Vendor_number_is_required_when_erp_validation_is_on()
    {
        await Intake().ProcessEmailAsync(Email("m1"), default);
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Workflow().RegisterAsync(_store.Cases[0].Id, Details() with { VendorNo = null }, AllYes(ChecklistStage.Intake), Dc, default));
        Assert.Equal(RuleCodes.MissingDetails, ex.Code);
    }

    [Fact]
    public async Task Reassignment_is_limited_to_the_category_route()
    {
        _store.Routes.Clear();
        _store.Routes.Add(new CategoryRoute("GMC", "GMC invoices", Ro.Id, Ro.DisplayName, ["ro-2"]));
        await Intake().ProcessEmailAsync(Email("m1"), default);
        var wf = Workflow();
        var id = _store.Cases[0].Id;
        await wf.RegisterAsync(id, Details(), AllYes(ChecklistStage.Intake), Dc, default);
        await wf.SendForVerificationAsync(id, Dc, default);
        var fm = Actor.Create("fm-1", "FM", VimRole.FinanceManager);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => wf.ReassignAsync(id, "someone-else", "Reviewer on leave this week", fm, default));
        Assert.Equal(RuleCodes.InvalidValue, ex.Code);

        await wf.ReassignAsync(id, "ro-2", "Reviewer on leave this week", fm, default);
        Assert.Equal("ro-2", _store.Cases[0].AssignedReviewerId);
    }

    [Fact]
    public async Task A_physical_scan_that_is_not_a_pdf_is_refused()
    {
        var file = new CaseWorkflowService.ScannedFile("scan.pdf", "application/pdf", _ => Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("GIF89a"))));
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Workflow().ReceivePhysicalAsync(Dc, null, "ABC Ltd", [file], default));
        Assert.Equal(RuleCodes.InvalidValue, ex.Code);
        Assert.Empty(_store.Cases);
    }

    [Fact]
    public async Task Scan_sync_records_results_and_flags_malware()
    {
        await Intake().ProcessEmailAsync(Email("m1"), default);
        var updated = await new DocumentScanSync(_store, new FakeScanReader(ScanStatus.Malicious), _store, _clock).RunAsync(default);

        Assert.Equal(1, updated);
        Assert.Equal(ScanStatus.Malicious, _store.Cases[0].Documents[0].ScanStatus);
        Assert.Contains(_store.Cases[0].Flags, f => f.Code == FlagCodes.MaliciousAttachment && f.Blocking);
    }

    [Fact]
    public async Task No_bank_account_in_erp_raises_an_advisory_flag()
    {
        _erp.Vendors["V0001"] = _erp.Vendors["V0001"] with { BankAccountNumbers = [] };
        await Intake().ProcessEmailAsync(Email("m1"), default);
        await Workflow().RegisterAsync(_store.Cases[0].Id, Details(), AllYes(ChecklistStage.Intake), Dc, default);
        Assert.Contains(_store.Cases[0].Flags, f => f.Code == FlagCodes.BankDetailsUnverified && !f.Blocking);
    }
}
