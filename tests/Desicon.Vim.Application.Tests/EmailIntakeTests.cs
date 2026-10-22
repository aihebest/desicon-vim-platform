using System.Text;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Cases;
using Desicon.Vim.Application.Intake;
using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Domain.Cases;
using Microsoft.Extensions.Logging.Abstractions;

namespace Desicon.Vim.Application.Tests;

/// <summary>Legacy email intake (switched off in production; kept working for the archive and as a fallback).</summary>
public class EmailIntakeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryStore _store = new();
    private readonly FakeErp _erp = new();
    private readonly FakeClock _clock = new(Now);

    public EmailIntakeTests()
    {
        _erp.Vendors["V0001"] = new ErpVendor("V0001", "ABC Ltd", false, ["accounts@abc.example"], ["0123456789"]);
    }

    private IntakeService Intake(ExtractedInvoiceFields? extracted = null) => new(
        _store, _store, _store, _store, new FakeExtractor(extracted), _erp, _store, _store, _clock, Opts.Default(), NullLogger<IntakeService>.Instance);

    private static InboundAttachment Pdf(string name = "invoice.pdf") =>
        new(name, "application/pdf", 20, _ => Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 test content"))));

    private static InboundEmail Email(string id, string subject = "ABC LTD_INV001_PO123_14Oct26", bool? auth = true, params InboundAttachment[] files) =>
        EmailFrom("accounts@abc.example", id, subject, auth, files);

    private static InboundEmail EmailFrom(string from, string id, string subject = "ABC LTD_INV001_PO123_14Oct26", bool? auth = true, params InboundAttachment[] files) =>
        new(id, from, "ABC Ltd", subject, Now, auth, files.Length == 0 ? [Pdf()] : files);

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
        c.ReturnToVendor("GRV is missing from the package.", ActionContext.For(Actor.System, Now));

        var result = await Intake().ProcessEmailAsync(Email("m5", subject: $"RE: {c.Reference} corrected GRV attached"), default);

        Assert.Equal(IntakeOutcome.AddedToResubmission, result.Outcome);
        Assert.Single(_store.Cases);
        Assert.Equal(CaseStatus.Received, c.Status);
        Assert.Equal(2, c.SubmissionRound);
        Assert.Equal(2, c.Documents.Count);
    }

    [Fact]
    public async Task Vendor_text_in_notifications_is_html_encoded()
    {
        await Intake().ProcessEmailAsync(Email("m1", subject: "<script>alert(1)</script>"), default);
        var c = _store.Cases[0];
        c.ReturnToVendor("Bad <b>doc</b> attached", ActionContext.For(Actor.System, Now));
        var returned = NotificationTemplates.ReturnedToVendor(c, "accounts@abc.example", "Bad <b>doc</b> attached");
        Assert.Contains("Bad &lt;b&gt;doc&lt;/b&gt; attached", returned.HtmlBody);
    }

    [Fact]
    public async Task A_stranger_quoting_a_reference_cannot_add_documents_to_that_case()
    {
        await Intake().ProcessEmailAsync(Email("m1"), default);
        var c = _store.Cases[0];
        c.ReturnToVendor("GRV is missing from the package.", ActionContext.For(Actor.System, Now));

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
    public async Task Scan_sync_records_results_and_flags_malware()
    {
        await Intake().ProcessEmailAsync(Email("m1"), default);
        var updated = await new DocumentScanSync(_store, new FakeScanReader(ScanStatus.Malicious), _store, _clock).RunAsync(default);

        Assert.Equal(1, updated);
        Assert.Equal(ScanStatus.Malicious, _store.Cases[0].Documents[0].ScanStatus);
        Assert.Contains(_store.Cases[0].Flags, f => f.Code == FlagCodes.MaliciousAttachment && f.Blocking);
    }
}
