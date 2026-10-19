using Desicon.Vim.Domain.Audit;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Checklists;
using Desicon.Vim.Domain.Workflow;
using static Desicon.Vim.Domain.Tests.TestData;

namespace Desicon.Vim.Domain.Tests;

public class InvoiceCaseWorkflowTests
{
    private static readonly ChecklistDefinition Checklist = DesiconInvoiceChecklist.Version1;

    private static string CodeOf(Action act) => Assert.Throws<DomainRuleException>(act).Code;

    [Fact]
    public void Happy_path_runs_from_receipt_to_closed()
    {
        var c = Verified();
        c.AcceptForProcessing(At(AccountsPayable));
        c.RecordErpEntry("PI-000123", At(AccountsPayable));
        c.RecordPayment("PAY-77", new DateOnly(2026, 10, 30), At(Actor.System));
        c.Close(At(Actor.System));

        Assert.Equal(CaseStatus.Closed, c.Status);
        Assert.Equal("PI-000123", c.ErpDocumentNumber);
        Assert.Equal(Reviewer.Id, c.VerifiedById);
        Assert.Equal(AccountsPayable.Id, c.ApProcessedById);
    }

    [Fact]
    public void Every_change_appends_a_valid_hash_chained_audit_event()
    {
        var c = Verified();
        var events = c.NewAuditEvents;

        Assert.True(events.Count >= 5);
        Assert.Equal("Received", events[0].Action);
        Assert.Equal(AuditEvent.GenesisHash, events[0].PreviousHash);
        Assert.True(AuditEvent.VerifyChain(events));
        Assert.Equal(events[^1].Hash, c.LastAuditHash);
    }

    [Fact]
    public void Removing_an_audit_event_breaks_the_chain()
    {
        var events = Verified().NewAuditEvents.ToList();
        events.RemoveAt(2);
        Assert.False(AuditEvent.VerifyChain(events));
    }

    [Fact]
    public void Register_requires_the_document_controller_role()
    {
        var c = NewCase();
        var code = CodeOf(() => c.Register(Details(), AllYes(ChecklistStage.Intake), Checklist, At(Reviewer)));
        Assert.Equal(RuleCodes.RoleNotPermitted, code);
    }

    [Fact]
    public void Register_rejects_an_incomplete_intake_checklist()
    {
        var c = NewCase();
        var answers = AllYes(ChecklistStage.Intake).Skip(1).ToList();
        Assert.Equal(RuleCodes.ChecklistIncomplete, CodeOf(() => c.Register(Details(), answers, Checklist, At(DocumentController))));
    }

    [Fact]
    public void Register_rejects_a_failed_mandatory_item_so_the_case_must_be_returned()
    {
        var c = NewCase();
        var answers = AllYes(ChecklistStage.Intake)
            .Select(a => a.ItemCode == "DOC_GRV" ? a with { Answer = ChecklistAnswer.No } : a)
            .ToList();
        Assert.Equal(RuleCodes.ChecklistFailed, CodeOf(() => c.Register(Details(), answers, Checklist, At(DocumentController))));
        Assert.Equal(CaseStatus.Received, c.Status);
    }

    [Fact]
    public void Services_and_materials_need_different_supporting_documents()
    {
        var materials = Checklist.ItemsFor(ChecklistStage.Intake, SupplyType.Materials).Select(i => i.Code).ToList();
        var services = Checklist.ItemsFor(ChecklistStage.Intake, SupplyType.Services).Select(i => i.Code).ToList();

        Assert.Contains("DOC_WAYBILL", materials);
        Assert.DoesNotContain("DOC_WAYBILL", services);
        Assert.Contains("DOC_WCC", services);
        Assert.DoesNotContain("DOC_WCC", materials);
    }

    [Fact]
    public void Timesheets_may_be_not_applicable_but_grv_may_not()
    {
        var c = NewCase();
        var answers = AllYes(ChecklistStage.Intake, SupplyType.Services)
            .Select(a => a.ItemCode is "DOC_TIMESHEETS" or "DOC_THIRD_PARTY" ? a with { Answer = ChecklistAnswer.NotApplicable } : a)
            .ToList();
        c.Register(Details(SupplyType.Services), answers, Checklist, At(DocumentController));
        Assert.Equal(CaseStatus.Registered, c.Status);

        var c2 = NewCase();
        var bad = AllYes(ChecklistStage.Intake, SupplyType.Services)
            .Select(a => a.ItemCode == "DOC_GRV" ? a with { Answer = ChecklistAnswer.NotApplicable } : a)
            .ToList();
        Assert.Equal(RuleCodes.ChecklistFailed, CodeOf(() => c2.Register(Details(SupplyType.Services), bad, Checklist, At(DocumentController))));
    }

    [Fact]
    public void Register_requires_complete_invoice_details()
    {
        var c = NewCase();
        var details = Details() with { InvoiceNumber = " ", Amount = 0 };
        Assert.Equal(RuleCodes.MissingDetails, CodeOf(() => c.Register(details, AllYes(ChecklistStage.Intake), Checklist, At(DocumentController))));
    }

    [Fact]
    public void Only_the_assigned_reviewer_or_delegate_can_verify()
    {
        var c = UnderVerification();
        Assert.Equal(RuleCodes.NotAssignedReviewer, CodeOf(() => c.Verify(AllYes(ChecklistStage.Verification), Checklist, At(OtherReviewer))));

        c.Verify(AllYes(ChecklistStage.Verification), Checklist, At(DelegateReviewer));
        Assert.Equal(CaseStatus.Verified, c.Status);
        Assert.Equal(DelegateReviewer.Id, c.VerifiedById);
    }

    [Fact]
    public void The_registrar_cannot_also_verify()
    {
        var both = Actor.Create("dual-1", "Dual role", VimRole.DocumentController, VimRole.Reviewer);
        var c = NewCase();
        c.Register(Details(), AllYes(ChecklistStage.Intake), Checklist, At(both));

        // Refused at routing: the registrar can never become the reviewer.
        var toSelf = new ReviewerAssignment(both.Id, both.DisplayName, [], Monday10am.AddHours(48), Monday10am.AddHours(24));
        Assert.Equal(RuleCodes.SegregationOfDuties, CodeOf(() => c.SendForVerification(toSelf, At(OtherDocumentController))));

        // And if the route names them as a delegate, Verify still refuses.
        c.SendForVerification(new ReviewerAssignment(Reviewer.Id, Reviewer.DisplayName, [both.Id], Monday10am.AddHours(48), Monday10am.AddHours(24)), At(OtherDocumentController));
        Assert.Equal(RuleCodes.SegregationOfDuties, CodeOf(() => c.Verify(AllYes(ChecklistStage.Verification), Checklist, At(both))));
    }

    [Fact]
    public void The_verifier_cannot_also_process_in_accounts_payable()
    {
        var both = Actor.Create("ro-ap", "RO who is also AP", VimRole.Reviewer, VimRole.AccountsPayable);
        var c = Registered();
        c.SendForVerification(new ReviewerAssignment(both.Id, both.DisplayName, [], Monday10am.AddHours(48), Monday10am.AddHours(24)), At(DocumentController));
        c.Verify(AllYes(ChecklistStage.Verification), Checklist, At(both));

        Assert.Equal(RuleCodes.SegregationOfDuties, CodeOf(() => c.AcceptForProcessing(At(both))));
        Assert.DoesNotContain(CaseAction.AcceptForProcessing, c.AvailableActionsFor(both));
    }

    [Fact]
    public void Accounts_payable_cannot_accept_while_a_blocking_flag_is_open_and_only_the_finance_manager_clears_it()
    {
        var c = Verified();
        var flag = c.RaiseFlag(FlagCodes.PossibleDuplicate, "Possible duplicate of VIM-2026-000000", blocking: true, At(Actor.System));

        Assert.Equal(RuleCodes.BlockingFlagOpen, CodeOf(() => c.AcceptForProcessing(At(AccountsPayable))));

        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => c.ResolveFlag(flag.Id, "Checked: different delivery, not a duplicate.", At(OtherDocumentController))));
        c.ResolveFlag(flag.Id, "Checked: different delivery, not a duplicate.", At(FinanceManager));
        c.AcceptForProcessing(At(AccountsPayable));
        Assert.Equal(CaseStatus.WithAccountsPayable, c.Status);
    }

    [Fact]
    public void Only_the_finance_manager_can_clear_a_bank_details_mismatch()
    {
        var c = Verified();
        var flag = c.RaiseFlag(FlagCodes.BankDetailsMismatch, "Account differs from vendor master", blocking: true, At(Actor.System));

        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => c.ResolveFlag(flag.Id, "Looks fine to me", At(AccountsPayable))));

        c.ResolveFlag(flag.Id, "Called vendor on known number; invoice account is a typo, pay to master.", At(FinanceManager));
        Assert.False(c.HasOpenBlockingFlags);
    }

    [Fact]
    public void Returning_requires_a_reason_and_clears_the_deadline()
    {
        var c = UnderVerification();
        Assert.Equal(RuleCodes.ReasonRequired, CodeOf(() => c.ReturnToVendor("", At(Reviewer))));

        c.ReturnToVendor("GRV is not signed by the authorised receiver.", At(Reviewer));
        Assert.Equal(CaseStatus.ReturnedToVendor, c.Status);
        Assert.Null(c.DueAtUtc);
        Assert.Equal("GRV is not signed by the authorised receiver.", c.LastReason);
    }

    [Fact]
    public void A_resubmission_reopens_the_same_case_in_a_new_round()
    {
        var c = UnderVerification();
        c.ReturnToVendor("GRV is not signed by the authorised receiver.", At(Reviewer));
        c.Resubmit("RE: VIM-2026-000001 corrected GRV", At(Actor.System));

        Assert.Equal(CaseStatus.Received, c.Status);
        Assert.Equal(2, c.SubmissionRound);
        Assert.Null(c.AssignedReviewerId);
        Assert.Equal("VIM-2026-000001", c.Reference);
    }

    [Fact]
    public void Wrong_status_is_rejected_with_a_clear_code()
    {
        var c = NewCase();
        Assert.Equal(RuleCodes.ActionNotAllowedInStatus, CodeOf(() => c.AcceptForProcessing(At(AccountsPayable))));
    }

    [Fact]
    public void Closed_cases_accept_no_new_documents()
    {
        var c = Verified();
        c.Cancel("Vendor withdrew the invoice.", At(FinanceManager));
        Assert.Equal(RuleCodes.ActionNotAllowedInStatus,
            CodeOf(() => c.AttachDocument(DocumentKind.Other, "late.pdf", "application/pdf", 10, "AA", "p", At(DocumentController))));
    }

    [Fact]
    public void Malware_raises_a_blocking_flag_and_blocks_registration()
    {
        var c = InvoiceCase.Receive("VIM-2026-000003", SubmissionChannel.Email, "x@y.example", null, null, At(Actor.System));
        c.AttachDocument(DocumentKind.SubmissionPackage, "bad.pdf", "application/pdf", 10, "AA", "p", At(Actor.System));
        c.RecordScanResult(c.Documents[0].Id, ScanStatus.Malicious, At(Actor.System));

        Assert.True(c.HasOpenBlockingFlags);
        Assert.Equal(RuleCodes.BlockingFlagOpen, CodeOf(() => c.Register(Details(), AllYes(ChecklistStage.Intake), Checklist, At(DocumentController))));
    }

    [Fact]
    public void Physical_submissions_can_only_be_logged_by_a_document_controller()
    {
        Assert.Equal(RuleCodes.RoleNotPermitted,
            CodeOf(() => InvoiceCase.Receive("VIM-2026-000002", SubmissionChannel.Physical, null, null, null, At(Reviewer))));
    }

    [Fact]
    public void Available_actions_reflect_role_status_and_assignment()
    {
        var c = UnderVerification();
        Assert.Contains(CaseAction.Verify, c.AvailableActionsFor(Reviewer));
        Assert.DoesNotContain(CaseAction.Verify, c.AvailableActionsFor(OtherReviewer));
        Assert.Contains(CaseAction.Reassign, c.AvailableActionsFor(FinanceManager));
        Assert.Empty(c.AvailableActionsFor(AccountsPayable));
    }

    [Fact]
    public void Raising_the_same_flag_twice_keeps_one_open_flag()
    {
        var c = NewCase();
        c.RaiseFlag(FlagCodes.UnknownSender, "Not a known vendor address", false, At(Actor.System));
        c.RaiseFlag(FlagCodes.UnknownSender, "Not a known vendor address", false, At(Actor.System));
        Assert.Single(c.Flags);
    }

    [Fact]
    public void The_registrar_cannot_also_process_in_accounts_payable()
    {
        var dcAp = Actor.Create("dc-ap", "DC who is also AP", VimRole.DocumentController, VimRole.AccountsPayable);
        var c = NewCase();
        c.Register(Details(), AllYes(ChecklistStage.Intake), Checklist, At(dcAp));
        c.SendForVerification(Assignment(), At(dcAp));
        c.Verify(AllYes(ChecklistStage.Verification), Checklist, At(Reviewer));

        Assert.Equal(RuleCodes.SegregationOfDuties, CodeOf(() => c.AcceptForProcessing(At(dcAp))));
    }

    [Fact]
    public void Whoever_clears_a_blocking_flag_cannot_then_process_the_case()
    {
        var fmAp = Actor.Create("fm-ap", "FM who is also AP", VimRole.FinanceManager, VimRole.AccountsPayable);
        var c = Verified();
        var flag = c.RaiseFlag(FlagCodes.BankDetailsMismatch, "Account differs", blocking: true, At(Actor.System));
        c.ResolveFlag(flag.Id, "Verified by phone with known contact.", At(fmAp));

        Assert.Equal(RuleCodes.SegregationOfDuties, CodeOf(() => c.AcceptForProcessing(At(fmAp))));
        c.AcceptForProcessing(At(AccountsPayable));
    }

    [Fact]
    public void A_case_cannot_be_assigned_to_the_person_routing_it_or_its_registrar()
    {
        var dcRo = Actor.Create("dc-ro", "Second DC who is also a reviewer", VimRole.DocumentController, VimRole.Reviewer);
        var c = UnderVerification();
        var toSelf = new ReviewerAssignment(dcRo.Id, dcRo.DisplayName, [], Monday10am.AddHours(48), Monday10am.AddHours(24));
        Assert.Equal(RuleCodes.SegregationOfDuties, CodeOf(() => c.Reassign(toSelf, "Covering while Chidi is away", At(dcRo))));

        var toRegistrar = new ReviewerAssignment(DocumentController.Id, "Ada", [], Monday10am.AddHours(48), Monday10am.AddHours(24));
        Assert.Equal(RuleCodes.SegregationOfDuties, CodeOf(() => c.Reassign(toRegistrar, "Covering while Chidi is away", At(FinanceManager))));
    }

    [Fact]
    public void Accounts_payable_cannot_act_until_every_document_is_scanned_clean()
    {
        var c = Verified();
        var late = c.AttachDocument(DocumentKind.Correspondence, "note.pdf", "application/pdf", 10, "BB", "p2", At(DocumentController));
        Assert.Equal(RuleCodes.BlockingFlagOpen, CodeOf(() => c.AcceptForProcessing(At(AccountsPayable))));

        c.RecordScanResult(late.Id, ScanStatus.Clean, At(Actor.System));
        c.AcceptForProcessing(At(AccountsPayable));
        Assert.Equal(CaseStatus.WithAccountsPayable, c.Status);
    }

    [Fact]
    public void Payment_reference_is_required()
    {
        var c = Verified();
        c.AcceptForProcessing(At(AccountsPayable));
        c.RecordErpEntry("PI-1", At(AccountsPayable));
        Assert.Equal(RuleCodes.MissingDetails, CodeOf(() => c.RecordPayment(" ", new DateOnly(2026, 10, 30), At(Actor.System))));
    }

    [Fact]
    public void Removing_the_latest_audit_event_is_detected_against_the_case_head()
    {
        var c = Verified();
        var events = c.NewAuditEvents.ToList();
        Assert.True(AuditEvent.VerifyChain(events, c.LastAuditHash));
        events.RemoveAt(events.Count - 1);
        Assert.True(AuditEvent.VerifyChain(events));
        Assert.False(AuditEvent.VerifyChain(events, c.LastAuditHash));
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
