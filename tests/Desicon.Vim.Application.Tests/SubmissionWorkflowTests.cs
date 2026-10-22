using System.Text;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Cases;
using Desicon.Vim.Application.Sla;
using Desicon.Vim.Application.Submission;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Checklists;
using Microsoft.Extensions.Logging.Abstractions;

namespace Desicon.Vim.Application.Tests;

/// <summary>Workflow v2 end to end: submit → automatic checks → Finance Manager (if flagged) → Accounts accepts → vendor told.</summary>
public class SubmissionWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero); // Monday 10:00 WAT
    private static readonly Actor Ap = Actor.Create("ap-1", "Efe (AP)", VimRole.AccountsPayable);
    private static readonly Actor Ap2 = Actor.Create("ap-2", "Gbenga (AP)", VimRole.AccountsPayable);
    private static readonly Actor Fm = Actor.Create("fm-1", "Femi (FM)", VimRole.FinanceManager);

    private readonly InMemoryStore _store = new();
    private readonly FakeErp _erp = new();
    private readonly FakeClock _clock = new(Now);

    public SubmissionWorkflowTests()
    {
        _erp.Vendors["V0001"] = new ErpVendor("V0001", "ABC Ltd", false, ["accounts@abc.example"], []);
    }

    private AutomaticChecks Checks() => new(_store, _erp, Opts.Default());

    private SubmissionService Submissions() => new(_store, _store, _store, _erp, Checks(), _store, _store, _clock, Opts.Default(), NullLogger<SubmissionService>.Instance);

    private SubmissionProcessor Processor() => new(_store, Checks(), _store, _store, _clock, Opts.Default(), NullLogger<SubmissionProcessor>.Instance);

    private CaseWorkflowService Workflow() => new(_store, _store, _store, _clock, Opts.Default());

    private static InvoiceSubmission Submission(string invoiceNo = "INV-001", decimal amount = 1_000_000m, string? email = null, string order = "PO123") =>
        new("V0001", "ABC Ltd", email, invoiceNo, new DateOnly(2026, 10, 14), amount, "NGN", order, SupplyType.Materials, new DateOnly(2026, 10, 13));

    private static SubmittedFile Pdf(DocumentKind kind) =>
        new(kind, $"{kind}.pdf", "application/pdf", 21, _ => Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 test content"))));

    private static IReadOnlyList<SubmittedFile> AllSlots() =>
        RequiredDocuments.For(SupplyType.Materials).Select(Pdf).ToList();

    private static IReadOnlyCollection<ChecklistResponse> AllYes() =>
        DesiconInvoiceChecklist.Current.ItemsFor(ChecklistStage.AccountsCheck, SupplyType.Materials).Select(i => new ChecklistResponse(i.Code, ChecklistAnswer.Yes)).ToList();

    private static void ScanAllClean(InvoiceCase c)
    {
        foreach (var d in c.Documents.Where(d => d.ScanStatus == ScanStatus.Pending))
        {
            c.RecordScanResult(d.Id, ScanStatus.Clean, ActionContext.For(Actor.System, Now));
        }
    }

    private async Task<InvoiceCase> SubmitAndScanAsync(InvoiceSubmission? s = null)
    {
        var c = await Submissions().SubmitAsync(Ap, SubmissionChannel.Assisted, s ?? Submission(), AllSlots(), default);
        ScanAllClean(c);
        return c;
    }

    [Fact]
    public async Task Assisted_submission_creates_a_case_and_acknowledges_the_vendor_at_the_bc_email()
    {
        var c = await Submissions().SubmitAsync(Ap, SubmissionChannel.Assisted, Submission(), AllSlots(), default);

        Assert.Equal(CaseStatus.Received, c.Status);
        Assert.Equal(4, c.Documents.Count);
        Assert.Equal("accounts@abc.example", c.SenderEmail);
        var ack = Assert.Single(_store.Sent, n => n.Template == "Acknowledgement");
        Assert.Equal("accounts@abc.example", ack.ToAddress);
        Assert.Contains("credit period counts from the acceptance date", ack.HtmlBody);
    }

    [Fact]
    public async Task Precheck_lists_every_missing_slot_and_non_pdf_file()
    {
        var notPdf = new SubmittedFile(DocumentKind.Invoice, "invoice.jpg", "image/jpeg", 3, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        var problems = await Submissions().PrecheckAsync(Submission(), [notPdf], null, null, default);

        Assert.Contains(problems, p => p.Contains("Goods Received Voucher", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Waybill", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("not a valid PDF", StringComparison.Ordinal));
        await Assert.ThrowsAsync<DomainRuleException>(() => Submissions().SubmitAsync(Ap, SubmissionChannel.Assisted, Submission(), [notPdf], default));
        Assert.Empty(_store.Cases);
    }

    [Fact]
    public async Task No_posted_receipt_is_refused_at_the_door()
    {
        _erp.Order = _erp.Order with { HasPostedReceipt = false, ReceivedNotInvoicedAmount = 0m };
        var problems = await Submissions().PrecheckAsync(Submission(), AllSlots(), null, null, default);
        Assert.Contains(problems, p => p.Contains("No delivery has been confirmed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(999_995, true)] // within the 10.00 rounding tolerance
    [InlineData(900_000, false)] // partial invoice
    [InlineData(1_100_000, false)] // above the received value
    public async Task Invoices_must_be_for_the_full_received_value(double amount, bool accepted)
    {
        var problems = await Submissions().PrecheckAsync(Submission(amount: (decimal)amount), AllSlots(), null, null, default);
        Assert.Equal(accepted, !problems.Any(p => p.Contains("full received value", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Wrong_vendor_order_and_blocked_vendor_are_refused()
    {
        _erp.Order = _erp.Order with { BelongsToVendor = false };
        Assert.Contains(await Submissions().PrecheckAsync(Submission(), AllSlots(), null, null, default), p => p.Contains("not issued to vendor", StringComparison.Ordinal));

        _erp.Vendors["V0001"] = _erp.Vendors["V0001"] with { Blocked = true };
        Assert.Contains(await Submissions().PrecheckAsync(Submission(), AllSlots(), null, null, default), p => p.Contains("on hold", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bc_down_does_not_stop_a_submission()
    {
        _erp.Down = true;
        var c = await Submissions().SubmitAsync(Ap, SubmissionChannel.Assisted, Submission(email: "ap@abc.example"), AllSlots(), default);
        Assert.Equal(CaseStatus.Received, c.Status);
    }

    [Fact]
    public async Task Processor_waits_for_the_scan_then_passes_a_clean_case_to_accounts()
    {
        var c = await Submissions().SubmitAsync(Ap, SubmissionChannel.Assisted, Submission(), AllSlots(), default);

        var first = await Processor().RunAsync(default);
        Assert.Equal(1, first.Waiting);
        Assert.Equal(CaseStatus.Received, c.Status);

        ScanAllClean(c);
        var second = await Processor().RunAsync(default);

        Assert.Equal(1, second.ToAccounts);
        Assert.Equal(CaseStatus.WithAccountsPayable, c.Status);
        Assert.Equal("Accounts Payable", c.AssignedReviewerName);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero), c.DueAtUtc); // one working day (9 working hours)
        Assert.Single(_store.Sent, n => n.Template == "ReadyForAccounts" && n.ToAddress == "ap@desicon.example");
    }

    [Fact]
    public async Task A_malicious_file_is_returned_to_the_vendor_with_the_reason()
    {
        var c = await Submissions().SubmitAsync(Ap, SubmissionChannel.Assisted, Submission(), AllSlots(), default);
        c.RecordScanResult(c.Documents[0].Id, ScanStatus.Malicious, ActionContext.For(Actor.System, Now));
        ScanAllClean(c);

        await Processor().RunAsync(default);

        Assert.Equal(CaseStatus.ReturnedToVendor, c.Status);
        Assert.Contains("virus scan", c.LastReason, StringComparison.Ordinal);
        Assert.Single(_store.Sent, n => n.Template == "ReturnedToVendor");
    }

    [Fact]
    public async Task Bc_finding_after_the_scan_returns_the_case_with_reasons()
    {
        var c = await SubmitAndScanAsync();
        _erp.Order = _erp.Order with { ReceivedNotInvoicedAmount = 500_000m };

        await Processor().RunAsync(default);

        Assert.Equal(CaseStatus.ReturnedToVendor, c.Status);
        Assert.Contains("full received value", c.LastReason, StringComparison.Ordinal);
        var notice = Assert.Single(_store.Sent, n => n.Template == "ReturnedToVendor");
        Assert.Contains("no credit period is running", notice.HtmlBody);
    }

    [Fact]
    public async Task The_same_invoice_number_twice_is_refused()
    {
        var first = await SubmitAndScanAsync();
        await Processor().RunAsync(default);
        Assert.Equal(CaseStatus.WithAccountsPayable, first.Status);

        var problems = await Submissions().PrecheckAsync(Submission(invoiceNo: "inv 001"), AllSlots(), null, null, default);
        Assert.Contains(problems, p => p.Contains(first.Reference, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_possible_duplicate_goes_to_the_finance_manager_who_releases_it_to_accounts()
    {
        var first = await SubmitAndScanAsync();
        await Processor().RunAsync(default);

        // Renumbered resend: same vendor, same amount, close date (on another PO, so the PO value check passes).
        var second = await SubmitAndScanAsync(Submission(invoiceNo: "INV-001A", order: "PO124"));
        await Processor().RunAsync(default);

        Assert.Equal(CaseStatus.ExceptionReview, second.Status);
        Assert.Equal("Finance Manager", second.AssignedReviewerName);
        Assert.Single(_store.Sent, n => n.Template == "ExceptionForFinanceManager" && n.ToAddress == "finance.manager@desicon.example");

        var flag = second.Flags.Single(f => f.IsOpen && f.Blocking);
        await Workflow().ResolveFlagAsync(second.Id, flag.Id, "Separate delivery; confirmed with Procurement", Fm, default);
        await Workflow().ReleaseToAccountsAsync(second.Id, Fm, default);

        Assert.Equal(CaseStatus.WithAccountsPayable, second.Status);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Bc_unreachable_waits_then_goes_to_the_finance_manager()
    {
        var c = await SubmitAndScanAsync();
        _erp.Down = true;

        await Processor().RunAsync(default);
        Assert.Equal(CaseStatus.Received, c.Status);

        _clock.UtcNow = Now.AddHours(5);
        await Processor().RunAsync(default);

        Assert.Equal(CaseStatus.ExceptionReview, c.Status);
        Assert.Contains(c.Flags, f => f.Code == FlagCodes.ErpUnavailable && f.Blocking);
    }

    [Fact]
    public async Task Acceptance_tells_the_vendor_and_starts_the_credit_period()
    {
        var c = await SubmitAndScanAsync();
        await Processor().RunAsync(default);
        _clock.UtcNow = Now.AddHours(2);

        await Workflow().AcceptAsync(c.Id, AllYes(), Ap2, default);

        Assert.Equal(CaseStatus.Accepted, c.Status);
        Assert.Equal(Now.AddHours(2), c.AcceptedAtUtc);
        var notice = Assert.Single(_store.Sent, n => n.Template == "Accepted");
        Assert.Equal("accounts@abc.example", notice.ToAddress);
        Assert.Contains("Mon 19 Oct 2026 12:00 WAT", notice.HtmlBody);
        Assert.Contains("credit period counts from it", notice.HtmlBody);
    }

    [Fact]
    public async Task The_officer_who_keyed_in_an_invoice_cannot_accept_it()
    {
        var c = await SubmitAndScanAsync();
        await Processor().RunAsync(default);

        await Assert.ThrowsAsync<DomainRuleException>(() => Workflow().AcceptAsync(c.Id, AllYes(), Ap, default));
    }

    [Fact]
    public async Task A_returned_case_can_be_resubmitted_with_a_corrected_file_and_is_checked_again()
    {
        var c = await SubmitAndScanAsync();
        await Processor().RunAsync(default);
        await Workflow().ReturnToVendorAsync(c.Id, "GRV is not signed by the receiver.", Ap2, default);

        await Submissions().ResubmitAsync(c.Id, Ap, Submission(), [Pdf(DocumentKind.GoodsReceivedVoucher)], default);
        Assert.Equal(CaseStatus.Received, c.Status);
        Assert.Equal(2, c.SubmissionRound);

        ScanAllClean(c);
        await Processor().RunAsync(default);
        Assert.Equal(CaseStatus.WithAccountsPayable, c.Status);
    }

    [Fact]
    public async Task Sla_reminds_the_holder_then_escalates_accounts_to_the_fm_and_the_fm_to_management()
    {
        var withAccounts = await SubmitAndScanAsync();
        await Processor().RunAsync(default);

        var flagged = await SubmitAndScanAsync(Submission(invoiceNo: "INV-001A", order: "PO124"));
        await Processor().RunAsync(default);
        Assert.Equal(CaseStatus.ExceptionReview, flagged.Status);

        var monitor = new SlaMonitor(_store, _store, _store, _clock, Opts.Default(), NullLogger<SlaMonitor>.Instance);

        _clock.UtcNow = Now.AddHours(5); // past half-way (4.5 working hours)
        var reminders = await monitor.RunAsync(default);
        Assert.Equal(2, reminders.RemindersSent);
        Assert.Contains(_store.Sent, n => n.Template == "Reminder" && n.ToAddress == "ap@desicon.example");
        Assert.Contains(_store.Sent, n => n.Template == "Reminder" && n.ToAddress == "finance.manager@desicon.example");

        _clock.UtcNow = Now.AddDays(1).AddHours(1); // past the one-working-day deadline
        var escalations = await monitor.RunAsync(default);
        Assert.Equal(2, escalations.Escalations);
        Assert.Contains(_store.Sent, n => n.Template == "Escalation" && n.ToAddress == "finance.manager@desicon.example" && n.CaseId == withAccounts.Id);
        Assert.Contains(_store.Sent, n => n.Template == "Escalation" && n.ToAddress == "dmd.office@desicon.example" && n.CaseId == flagged.Id);
    }

    [Fact]
    public async Task A_second_invoice_against_value_already_being_invoiced_is_refused()
    {
        var first = await SubmitAndScanAsync();
        await Processor().RunAsync(default);

        var problems = await Submissions().PrecheckAsync(Submission(invoiceNo: "INV-002"), AllSlots(), null, null, default);

        Assert.Contains(problems, p => p.Contains("already being invoiced", StringComparison.Ordinal) && p.Contains(first.Reference, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_resubmission_clears_flags_the_checks_raised_on_the_earlier_round()
    {
        var c = await SubmitAndScanAsync();
        _erp.Down = true;
        _clock.UtcNow = Now.AddHours(5);
        await Processor().RunAsync(default);
        Assert.Equal(CaseStatus.ExceptionReview, c.Status);
        await Workflow().ReturnToVendorAsync(c.Id, "Please confirm the PO number with Procurement.", Fm, default);

        _erp.Down = false;
        await Submissions().ResubmitAsync(c.Id, Ap, Submission(), [Pdf(DocumentKind.Invoice)], default);
        ScanAllClean(c);
        await Processor().RunAsync(default);

        Assert.DoesNotContain(c.Flags, f => f.IsOpen);
        Assert.Equal(CaseStatus.WithAccountsPayable, c.Status);
    }

    [Fact]
    public async Task With_business_central_switched_off_the_case_goes_to_the_finance_manager()
    {
        _erp.IsEnabled = false;
        var c = await SubmitAndScanAsync(Submission(email: "ap@abc.example"));

        await Processor().RunAsync(default);

        Assert.Equal(CaseStatus.ExceptionReview, c.Status);
        Assert.Contains(c.Flags, f => f.Code == FlagCodes.ErpUnavailable && f.Blocking);
    }

    [Fact]
    public async Task Anyone_who_keyed_in_any_round_cannot_accept()
    {
        var c = await SubmitAndScanAsync();
        await Processor().RunAsync(default);
        await Workflow().ReturnToVendorAsync(c.Id, "GRV is not signed by the receiver.", Ap2, default);
        await Submissions().ResubmitAsync(c.Id, Ap2, Submission(), [Pdf(DocumentKind.GoodsReceivedVoucher)], default);
        ScanAllClean(c);
        await Processor().RunAsync(default);

        // Ap keyed round 1, Ap2 keyed round 2: neither may accept.
        await Assert.ThrowsAsync<DomainRuleException>(() => Workflow().AcceptAsync(c.Id, AllYes(), Ap, default));
        await Assert.ThrowsAsync<DomainRuleException>(() => Workflow().AcceptAsync(c.Id, AllYes(), Ap2, default));
    }
}
