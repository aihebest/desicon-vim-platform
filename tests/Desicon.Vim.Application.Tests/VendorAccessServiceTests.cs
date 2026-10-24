using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.VendorAccess;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.VendorAccess;
using Microsoft.Extensions.Logging.Abstractions;

namespace Desicon.Vim.Application.Tests;

public class VendorAccessServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly Actor Procurement = Actor.Create("pr-1", "Halima (Procurement)", VimRole.Procurement);
    private static readonly Actor Ap = Actor.Create("ap-1", "Efe (AP)", VimRole.AccountsPayable);
    private static readonly Actor Fm = Actor.Create("fm-1", "Femi (FM)", VimRole.FinanceManager);

    private readonly InMemoryStore _store = new();
    private readonly FakeErp _erp = new();
    private readonly FakeClock _clock = new(Now);

    public VendorAccessServiceTests()
    {
        _erp.Vendors["V0001"] = new ErpVendor("V0001", "ABC Ltd", false, ["accounts@abc.example"], []);
        _erp.Vendors["V0002"] = new ErpVendor("V0002", "XYZ Ltd", false, ["finance@xyz.example"], []);
    }

    private VendorAccessService Service() => new(_store, _erp, _store, _store, _clock, Opts.Default(), NullLogger<VendorAccessService>.Instance);

    /// <summary>Invites and returns the token from the emailed link.</summary>
    private async Task<string> InviteAsync(string vendorNo = "V0001", string email = "accounts@abc.example")
    {
        await Service().InviteAsync(Procurement, vendorNo, email, ContactConfirmation.VendorCardEmail, null, default);
        var mail = _store.Sent.Last(n => n.Template == "PortalInvitation");
        var start = mail.HtmlBody.IndexOf("/invite/", StringComparison.Ordinal) + "/invite/".Length;
        var end = mail.HtmlBody.IndexOf('"', start);
        return mail.HtmlBody[start..end];
    }

    [Fact]
    public async Task Procurement_invites_a_card_contact_and_the_email_carries_a_one_time_link()
    {
        var token = await InviteAsync();

        var invitation = Assert.Single(_store.Invitations);
        Assert.Equal("V0001", invitation.VendorNo);
        Assert.Equal(VendorInvitation.HashToken(token), invitation.TokenHash);
        var mail = Assert.Single(_store.Sent);
        Assert.Equal("accounts@abc.example", mail.ToAddress);
        Assert.Contains("https://vendors.desicon.example/invite/", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(_store.AccessEvents, e => e.Action == "Invited");
    }

    [Fact]
    public async Task Accounts_payable_cannot_invite_and_blocked_vendors_cannot_be_invited()
    {
        await Assert.ThrowsAsync<DomainRuleException>(() => Service().InviteAsync(Ap, "V0001", "accounts@abc.example", ContactConfirmation.VendorCardEmail, null, default));

        _erp.Vendors["V0001"] = _erp.Vendors["V0001"] with { Blocked = true };
        await Assert.ThrowsAsync<DomainRuleException>(() => Service().InviteAsync(Procurement, "V0001", "accounts@abc.example", ContactConfirmation.VendorCardEmail, null, default));
        Assert.Empty(_store.Sent);
    }

    [Fact]
    public async Task A_new_invitation_replaces_the_pending_one_for_the_same_person()
    {
        var first = await InviteAsync();
        await InviteAsync();

        Assert.Single(_store.Invitations, i => i.IsPending(Now));
        var result = await Service().AcceptInvitationAsync(first, new PortalSignIn("tid:oid-1", "accounts@abc.example"), default);
        Assert.Equal(PortalAccess.NotLinked, result.Access);
    }

    [Fact]
    public async Task Accepting_binds_the_login_to_the_vendor_and_later_requests_are_allowed()
    {
        var token = await InviteAsync();
        var signIn = new PortalSignIn("tid:oid-1", "accounts@abc.example");

        var accepted = await Service().AcceptInvitationAsync(token, signIn, default);
        Assert.Equal(PortalAccess.Allowed, accepted.Access);
        Assert.Equal("V0001", accepted.User!.VendorNo);

        var later = await Service().ResolveAsync(signIn, default);
        Assert.Equal(PortalAccess.Allowed, later.Access);
        Assert.Equal("V0001", later.User!.ToActor().VendorNo);
    }

    [Fact]
    public async Task A_forwarded_link_does_not_work_for_another_email()
    {
        var token = await InviteAsync();
        var result = await Service().AcceptInvitationAsync(token, new PortalSignIn("tid:oid-9", "someone.else@gmail.example"), default);

        Assert.Equal(PortalAccess.NotLinked, result.Access);
        Assert.Empty(_store.VendorUsers);
        Assert.Contains(_store.AccessEvents, e => e.Action == "AcceptRefused");
    }

    [Fact]
    public async Task One_login_cannot_be_linked_to_two_vendors()
    {
        await Service().AcceptInvitationAsync(await InviteAsync(), new PortalSignIn("tid:oid-1", "accounts@abc.example"), default);

        await Service().InviteAsync(Procurement, "V0002", "accounts@abc.example", ContactConfirmation.PhoneCallBack, "Called Mr Ade on 0803 000 0000 (on file), 24 Oct", default);
        var mail = _store.Sent.Last(n => n.Template == "PortalInvitation");
        var start = mail.HtmlBody.IndexOf("/invite/", StringComparison.Ordinal) + "/invite/".Length;
        var token = mail.HtmlBody[start..mail.HtmlBody.IndexOf('"', start)];

        var result = await Service().AcceptInvitationAsync(token, new PortalSignIn("tid:oid-1", "accounts@abc.example"), default);
        Assert.Equal(PortalAccess.NotLinked, result.Access);
        Assert.Equal("V0001", Assert.Single(_store.VendorUsers).VendorNo);
    }

    [Fact]
    public async Task Unlinked_removed_blocked_and_dormant_users_are_refused()
    {
        Assert.Equal(PortalAccess.NotLinked, (await Service().ResolveAsync(new PortalSignIn("tid:nobody", "x@y.example"), default)).Access);

        var signIn = new PortalSignIn("tid:oid-1", "accounts@abc.example");
        await Service().AcceptInvitationAsync(await InviteAsync(), signIn, default);

        _erp.Vendors["V0001"] = _erp.Vendors["V0001"] with { Blocked = true };
        Assert.Equal(PortalAccess.VendorBlocked, (await Service().ResolveAsync(signIn, default)).Access);
        _erp.Vendors["V0001"] = _erp.Vendors["V0001"] with { Blocked = false };

        _clock.UtcNow = Now.AddDays(91);
        Assert.Equal(PortalAccess.Disabled, (await Service().ResolveAsync(signIn, default)).Access);
        Assert.Contains(_store.AccessEvents, e => e.Action == "DormantDisabled");
    }

    [Fact]
    public async Task A_removed_user_regains_access_only_through_a_new_invitation()
    {
        var signIn = new PortalSignIn("tid:oid-1", "accounts@abc.example");
        await Service().AcceptInvitationAsync(await InviteAsync(), signIn, default);
        var user = Assert.Single(_store.VendorUsers);

        await Service().DisableUserAsync(user.Id, "Reported as a leaver", Fm, default);
        Assert.Equal(PortalAccess.Disabled, (await Service().ResolveAsync(signIn, default)).Access);

        var again = await Service().AcceptInvitationAsync(await InviteAsync(), signIn, default);
        Assert.Equal(PortalAccess.Allowed, again.Access);
        Assert.False(user.IsDisabled);
    }

    [Fact]
    public async Task Staff_addresses_can_never_be_invited()
    {
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().InviteAsync(Procurement, "V0001", "halima@desicon.example", ContactConfirmation.PhoneCallBack, "Called the vendor back on 0803 000 0000", default));
        Assert.Equal(RuleCodes.SegregationOfDuties, ex.Code);

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().InviteAsync(Procurement, "V0001", "x@mail.desicon.example", ContactConfirmation.PhoneCallBack, "Called the vendor back on 0803 000 0000", default));
        Assert.Empty(_store.Invitations);
    }

    [Fact]
    public async Task A_call_back_invitation_is_reported_to_the_finance_manager()
    {
        await Service().InviteAsync(Procurement, "V0001", "ops@abc-partner.example", ContactConfirmation.PhoneCallBack, "Called Mr Ade on 0803 000 0000 (on file), 24 Oct", default);

        var notice = Assert.Single(_store.Sent, n => n.Template == "PortalCallBackInvitationNotice");
        Assert.Equal("finance.manager@desicon.example", notice.ToAddress);
        Assert.Contains("ops@abc-partner.example", notice.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("/invite/", notice.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Withdrawing_an_accepted_invitation_is_refused_and_removal_withdraws_pending_ones()
    {
        var token = await InviteAsync();
        await Service().AcceptInvitationAsync(token, new PortalSignIn("tid:oid-1", "accounts@abc.example"), default);
        var accepted = Assert.Single(_store.Invitations);
        await Assert.ThrowsAsync<DomainRuleException>(() => Service().WithdrawInvitationAsync(accepted.Id, Procurement, default));

        var second = await InviteAsync();
        var user = Assert.Single(_store.VendorUsers);
        await Service().DisableUserAsync(user.Id, "Contact left the company", Procurement, default);

        Assert.DoesNotContain(_store.Invitations, i => i.IsPending(Now));
        var again = await Service().AcceptInvitationAsync(second, new PortalSignIn("tid:oid-1", "accounts@abc.example"), default);
        Assert.NotEqual(PortalAccess.Allowed, again.Access);
    }
}
