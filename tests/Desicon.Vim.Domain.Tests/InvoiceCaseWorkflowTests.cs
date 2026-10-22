using Desicon.Vim.Domain.Audit;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Checklists;
using Desicon.Vim.Domain.Workflow;
using static Desicon.Vim.Domain.Tests.TestData;

namespace Desicon.Vim.Domain.Tests;

/// <summary>Workflow v2: vendor submits → automatic checks → (Finance Manager if flagged) → Accounts accepts → ERP → paid.</summary>
public class InvoiceCaseWorkflowTests
{
    private static readonly ChecklistDefinition Checklist = DesiconInvoiceChecklist.Current;

    private static string CodeOf(Action act) => Assert.Throws<DomainRuleException>(act).Code;

    [Fact]
    public void Happy_path_runs_from_submission_to_closed()
    {
        var c = Accepted();
        c.RecordErpEntry("PI-000123", At(AccountsPayable));
        c.RecordPayment("PAY-77", new DateOnly(2026, 10, 30), At(Actor.System));
        c.Close(At(Actor.System));

        Assert.Equal(CaseStatus.Closed, c.Status);
        Assert.Equal("PI-000123", c.ErpDocumentNumber);
        Assert.Equal(AccountsPayable.Id, c.ApProcessedById);
        Assert.Equal(Vendor.Id, c.RegisteredById);
    }

    [Fact]
    public void Acceptance_records_the_official_submission_time_for_credit_days()
    {
        var c = WithAccounts();
        Assert.Null(c.AcceptedAtUtc);
        var at = Monday10am.AddHours(3);

        c.Accept(AllYes(), Checklist, At(AccountsPayable, at));

        Assert.Equal(CaseStatus.Accepted, c.Status);
        Assert.Equal(at, c.AcceptedAtUtc);
        Assert.Null(c.DueAtUtc);
    }

    [Fact]
    public void Submission_records_the_details_and_delivery_date()
    {
        var c = Submitted();

        Assert.Equal(CaseStatus.Received, c.Status);
        Assert.Equal("V0001", c.VendorNo);
        Assert.Equal("PO123", c.OrderNumber);
        Assert.Equal(new DateOnly(2026, 10, 13), c.DeliveredOn);
        Assert.Equal("accounts@abc.example", c.SenderEmail);
        Assert.Empty(c.MissingRequiredDocuments);
        Assert.True(c.AllCurrentDocumentsScanned);
    }

    [Fact]
    public void Only_a_vendor_can_submit_on_the_portal_and_only_accounts_can_assist()
    {
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => InvoiceCase.Submit("VIM-2026-000009", SubmissionChannel.Portal, Submission(), At(AccountsPayable))));
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => InvoiceCase.Submit("VIM-2026-000009", SubmissionChannel.Assisted, Submission(), At(Vendor))));
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => InvoiceCase.Submit("VIM-2026-000009", SubmissionChannel.Email, Submission(), At(DocumentController))));

        var assisted = InvoiceCase.Submit("VIM-2026-000010", SubmissionChannel.Assisted, Submission(), At(AccountsPayable));
        Assert.Equal(SubmissionChannel.Assisted, assisted.Channel);
        Assert.Equal(AccountsPayable.Id, assisted.RegisteredById);
    }

    [Fact]
    public void Submission_needs_complete_details_and_no_future_dates()
    {
        var future = Submission() with { DeliveredOn = new DateOnly(2026, 12, 1) };
        Assert.Equal(RuleCodes.MissingDetails, CodeOf(() => InvoiceCase.Submit("VIM-2026-000009", SubmissionChannel.Portal, future, At(Vendor))));

        var noOrder = Submission() with { OrderNumber = " " };
        Assert.Equal(RuleCodes.MissingDetails, CodeOf(() => InvoiceCase.Submit("VIM-2026-000009", SubmissionChannel.Portal, noOrder, At(Vendor))));
    }

    [Fact]
    public void Materials_and_services_have_different_required_slots()
    {
        Assert.Contains(DocumentKind.GoodsReceivedVoucher, RequiredDocuments.For(SupplyType.Materials));
        Assert.Contains(DocumentKind.WaybillOrDeliveryNote, RequiredDocuments.For(SupplyType.Materials));
        Assert.Contains(DocumentKind.WorkCompletionCertificate, RequiredDocuments.For(SupplyType.Services));
        Assert.DoesNotContain(DocumentKind.Timesheet, RequiredDocuments.For(SupplyType.Services));

        var c = InvoiceCase.Submit("VIM-2026-000009", SubmissionChannel.Portal, Submission(), At(Vendor));
        c.AttachDocument(DocumentKind.Invoice, "inv.pdf", "application/pdf", 10, "X", "p/inv.pdf", At(Vendor));
        Assert.Contains(DocumentKind.GoodsReceivedVoucher, c.MissingRequiredDocuments);
    }

    [Fact]
    public void A_case_cannot_pass_to_accounts_with_a_missing_slot_or_an_unscanned_file()
    {
        var c = InvoiceCase.Submit("VIM-2026-000009", SubmissionChannel.Portal, Submission(), At(Vendor));
        c.AttachDocument(DocumentKind.Invoice, "inv.pdf", "application/pdf", 10, "X", "p/inv.pdf", At(Vendor));
        Assert.Equal(RuleCodes.MissingDocuments, CodeOf(() => c.PassToAccounts(Due(), At(Actor.System))));

        var pending = Submitted(scanClean: false);
        Assert.False(pending.AllCurrentDocumentsScanned);
        Assert.Equal(RuleCodes.BlockingFlagOpen, CodeOf(() => pending.PassToAccounts(Due(), At(Actor.System))));
    }

    [Fact]
    public void Only_the_system_moves_a_new_submission_on()
    {
        var c = Submitted();
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => c.PassToAccounts(Due(), At(AccountsPayable))));
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => c.ReturnToVendor("Missing GRV", At(AccountsPayable))));
    }

    [Fact]
    public void Passing_to_accounts_sets_the_accounts_deadline()
    {
        var c = WithAccounts();
        Assert.Equal(CaseStatus.WithAccountsPayable, c.Status);
        Assert.Equal("Accounts Payable", c.AssignedReviewerName);
        Assert.Equal(Monday10am.AddHours(9), c.DueAtUtc);
        Assert.Equal(Monday10am.AddHours(4.5), c.ReminderAtUtc);
    }

    [Fact]
    public void Automatic_rejection_returns_the_case_with_reasons_and_no_deadline()
    {
        var c = Submitted();
        c.ReturnToVendor("No GRV posted against PO123 yet.", At(Actor.System));

        Assert.Equal(CaseStatus.ReturnedToVendor, c.Status);
        Assert.Equal("No GRV posted against PO123 yet.", c.LastReason);
        Assert.Null(c.DueAtUtc);
        Assert.Null(c.AcceptedAtUtc);
    }

    [Fact]
    public void A_flagged_submission_goes_to_the_finance_manager_who_clears_and_releases_it()
    {
        var c = Submitted();
        Assert.Equal(RuleCodes.InvalidValue, CodeOf(() => c.RaiseException(Due("Finance Manager"), At(Actor.System))));

        var flag = c.RaiseFlag(FlagCodes.PossibleDuplicate, "Possible duplicate of VIM-2026-000003.", blocking: true, At(Actor.System));
        c.RaiseException(Due("Finance Manager"), At(Actor.System));
        Assert.Equal(CaseStatus.ExceptionReview, c.Status);

        Assert.Equal(RuleCodes.BlockingFlagOpen, CodeOf(() => c.ReleaseToAccounts(Due(), At(FinanceManager))));
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => c.ResolveFlag(flag.Id, "Checked by phone with the vendor", At(AccountsPayable))));

        c.ResolveFlag(flag.Id, "Different delivery; confirmed with Procurement", At(FinanceManager));
        c.ReleaseToAccounts(Due(), At(FinanceManager));
        Assert.Equal(CaseStatus.WithAccountsPayable, c.Status);
    }

    [Fact]
    public void The_finance_manager_can_reject_an_exception_with_a_reason()
    {
        var c = Submitted();
        c.RaiseFlag(FlagCodes.PossibleDuplicate, "Possible duplicate.", blocking: true, At(Actor.System));
        c.RaiseException(Due("Finance Manager"), At(Actor.System));

        Assert.Equal(RuleCodes.ReasonRequired, CodeOf(() => c.ReturnToVendor("no", At(FinanceManager))));
        c.ReturnToVendor("Duplicate of VIM-2026-000003, already accepted.", At(FinanceManager));
        Assert.Equal(CaseStatus.ReturnedToVendor, c.Status);
    }

    [Fact]
    public void Whoever_clears_a_blocking_flag_cannot_then_accept_the_case()
    {
        var both = Actor.Create("fm-ap", "Hauwa (FM and AP)", VimRole.FinanceManager, VimRole.AccountsPayable);
        var c = Submitted();
        var flag = c.RaiseFlag(FlagCodes.PossibleDuplicate, "Possible duplicate.", blocking: true, At(Actor.System));
        c.RaiseException(Due("Finance Manager"), At(Actor.System));
        c.ResolveFlag(flag.Id, "Not a duplicate; different PO", At(both));
        c.ReleaseToAccounts(Due(), At(both));

        Assert.Equal(RuleCodes.SegregationOfDuties, CodeOf(() => c.Accept(AllYes(), Checklist, At(both))));
        Assert.DoesNotContain(CaseAction.Accept, c.AvailableActionsFor(both));
        c.Accept(AllYes(), Checklist, At(AccountsPayable));
    }

    [Fact]
    public void Accounts_cannot_accept_an_invoice_they_keyed_in_for_the_vendor()
    {
        var c = WithAccounts(submittedBy: AccountsPayable, channel: SubmissionChannel.Assisted);

        Assert.Equal(RuleCodes.SegregationOfDuties, CodeOf(() => c.Accept(AllYes(), Checklist, At(AccountsPayable))));
        Assert.DoesNotContain(CaseAction.Accept, c.AvailableActionsFor(AccountsPayable));
        c.Accept(AllYes(), Checklist, At(OtherAccountsPayable));
        Assert.Equal(CaseStatus.Accepted, c.Status);
    }

    [Fact]
    public void Acceptance_needs_the_accounts_checklist_complete_and_passed()
    {
        var c = WithAccounts();
        Assert.Equal(RuleCodes.ChecklistIncomplete, CodeOf(() => c.Accept([], Checklist, At(AccountsPayable))));

        var failed = AllYes().Select(a => a.ItemCode == "ACC_BANK" ? a with { Answer = ChecklistAnswer.No } : a).ToList();
        Assert.Equal(RuleCodes.ChecklistFailed, CodeOf(() => c.Accept(failed, Checklist, At(AccountsPayable))));

        var vatNa = AllYes().Select(a => a.ItemCode == "ACC_VAT" ? a with { Answer = ChecklistAnswer.NotApplicable } : a).ToList();
        c.Accept(vatNa, Checklist, At(AccountsPayable));
        Assert.Equal(CaseStatus.Accepted, c.Status);
    }

    [Fact]
    public void Accounts_can_reject_with_reasons_instead_of_accepting()
    {
        var c = WithAccounts();
        c.ReturnToVendor("Invoice is not on letter-headed paper.", At(AccountsPayable));

        Assert.Equal(CaseStatus.ReturnedToVendor, c.Status);
        Assert.Null(c.AcceptedAtUtc);
    }

    [Fact]
    public void A_resubmission_reopens_the_same_case_and_corrected_files_replace_earlier_ones()
    {
        var c = Submitted();
        var oldGrv = c.Documents.Single(d => d.Kind == DocumentKind.GoodsReceivedVoucher);
        c.ReturnToVendor("GRV is not signed.", At(Actor.System));

        c.Resubmit(Submission(amount: 1_250_000m), At(Vendor));
        var newGrv = c.AttachDocument(DocumentKind.GoodsReceivedVoucher, "grv-signed.pdf", "application/pdf", 2048, "NEW", "p/grv2.pdf", At(Vendor));

        Assert.Equal(CaseStatus.Received, c.Status);
        Assert.Equal(2, c.SubmissionRound);
        Assert.False(c.AllCurrentDocumentsScanned);
        Assert.Contains(c.CurrentDocuments, d => d.Id == newGrv.Id);
        Assert.DoesNotContain(c.CurrentDocuments, d => d.Id == oldGrv.Id);
        Assert.Contains(c.CurrentDocuments, d => d.Kind == DocumentKind.Invoice && d.SubmissionRound == 1);
    }

    [Fact]
    public void A_resubmission_must_be_for_the_same_vendor()
    {
        var c = Submitted();
        c.ReturnToVendor("GRV is not signed.", At(Actor.System));
        Assert.Equal(RuleCodes.InvalidValue, CodeOf(() => c.Resubmit(Submission() with { VendorNo = "V0002" }, At(Vendor))));
    }

    [Fact]
    public void A_malicious_file_raises_a_blocking_flag()
    {
        var c = Submitted(scanClean: false);
        var doc = c.Documents[0];
        c.RecordScanResult(doc.Id, ScanStatus.Malicious, At(Actor.System));

        Assert.True(c.HasOpenBlockingFlags);
        Assert.Contains(c.Flags, f => f.Code == FlagCodes.MaliciousAttachment);
    }

    [Fact]
    public void Every_change_appends_a_valid_hash_chained_audit_event()
    {
        var c = Accepted();
        var events = c.NewAuditEvents;

        Assert.Equal("Submitted", events[0].Action);
        Assert.Equal(AuditEvent.GenesisHash, events[0].PreviousHash);
        Assert.True(AuditEvent.VerifyChain(events));
        Assert.Equal(events[^1].Hash, c.LastAuditHash);
        Assert.Contains(events, e => e.Action == nameof(CaseAction.Accept));
    }

    [Fact]
    public void Removing_an_audit_event_breaks_the_chain()
    {
        var events = Accepted().NewAuditEvents.ToList();
        events.RemoveAt(2);
        Assert.False(AuditEvent.VerifyChain(events));
    }

    [Fact]
    public void Removing_the_latest_audit_event_is_detected_against_the_case_head()
    {
        var c = Accepted();
        var events = c.NewAuditEvents.ToList();
        Assert.True(AuditEvent.VerifyChain(events, c.LastAuditHash));
        events.RemoveAt(events.Count - 1);
        Assert.True(AuditEvent.VerifyChain(events));
        Assert.False(AuditEvent.VerifyChain(events, c.LastAuditHash));
    }

    [Fact]
    public void Wrong_status_is_rejected_with_a_clear_code()
    {
        var c = Submitted();
        Assert.Equal(RuleCodes.ActionNotAllowedInStatus, CodeOf(() => c.RecordErpEntry("PI-1", At(AccountsPayable))));
        Assert.Equal(RuleCodes.ActionNotAllowedInStatus, CodeOf(() => c.Accept(AllYes(), Checklist, At(AccountsPayable))));
    }

    [Fact]
    public void Closed_cases_accept_no_new_documents()
    {
        var c = Accepted();
        c.RecordErpEntry("PI-1", At(AccountsPayable));
        c.RecordPayment("PAY-1", new DateOnly(2026, 10, 30), At(Actor.System));
        c.Close(At(Actor.System));

        Assert.Equal(RuleCodes.ActionNotAllowedInStatus, CodeOf(() => c.AttachDocument(DocumentKind.Other, "x.pdf", "application/pdf", 10, "X", "p/x.pdf", At(AccountsPayable))));
    }

    [Fact]
    public void Only_the_finance_manager_cancels()
    {
        var c = WithAccounts();
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => c.Cancel("Raised in error", At(AccountsPayable))));
        c.Cancel("Raised in error by the vendor", At(FinanceManager));
        Assert.Equal(CaseStatus.Cancelled, c.Status);
    }

    [Fact]
    public void Available_actions_reflect_role_and_status()
    {
        Assert.Empty(Submitted().AvailableActionsFor(AccountsPayable));
        Assert.Contains(CaseAction.Accept, WithAccounts().AvailableActionsFor(AccountsPayable));
        Assert.Contains(CaseAction.ReturnToVendor, WithAccounts().AvailableActionsFor(AccountsPayable));
        Assert.Contains(CaseAction.RecordErpEntry, Accepted().AvailableActionsFor(AccountsPayable));
        Assert.Empty(WithAccounts().AvailableActionsFor(Vendor));
    }

    [Fact]
    public void Raising_the_same_flag_twice_keeps_one_open_flag()
    {
        var c = Submitted();
        c.RaiseFlag(FlagCodes.PossibleDuplicate, "Possible duplicate.", blocking: true, At(Actor.System));
        c.RaiseFlag(FlagCodes.PossibleDuplicate, "Possible duplicate.", blocking: true, At(Actor.System));
        Assert.Single(c.Flags, f => f.IsOpen);
    }

    [Fact]
    public void Payment_reference_is_required()
    {
        var c = Accepted();
        c.RecordErpEntry("PI-1", At(AccountsPayable));
        Assert.Equal(RuleCodes.MissingDetails, CodeOf(() => c.RecordPayment(" ", new DateOnly(2026, 10, 30), At(Actor.System))));
    }

    [Fact]
    public void Routing_changes_need_the_finance_manager_and_are_recorded()
    {
        var route = new CategoryRoute("GMC", "GMC invoices", "ro-1", "Chidi");
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => route.Update("GMC invoices", "ro-9", "Uche", [], true, At(DocumentController))));

        var change = route.Update("GMC invoices", "ro-9", "Uche", ["ro-2"], true, At(FinanceManager));
        Assert.NotNull(change);
        Assert.Equal(FinanceManager.Id, change!.ChangedById);
        Assert.Contains("ro-1", change.OldValueJson);
        Assert.Contains("ro-9", change.NewValueJson);
        Assert.Null(route.Update("GMC invoices", "ro-9", "Uche", ["ro-2"], true, At(FinanceManager)));
    }

    [Fact]
    public void Creating_a_missing_route_needs_the_finance_manager_and_is_recorded()
    {
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => CategoryRoute.Create("pmc", "PMC project invoices", "ro-1", "Chidi", [], true, At(DocumentController))));

        var (route, change) = CategoryRoute.Create("pmc", "PMC project invoices", " ro-1 ", "Chidi", [" ro-2 ", ""], true, At(FinanceManager));
        Assert.Equal("PMC", route.Code);
        Assert.Equal("ro-1", route.ReviewerId);
        Assert.Equal(["ro-2"], route.DelegateIdList);
        Assert.Equal("PMC", change.RouteCode);
        Assert.Equal(FinanceManager.Id, change.ChangedById);
        Assert.Equal("{}", change.OldValueJson);
        Assert.Contains("ro-1", change.NewValueJson);
    }
}
