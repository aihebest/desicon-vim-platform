using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.VendorAccess;
using static Desicon.Vim.Domain.Tests.TestData;

namespace Desicon.Vim.Domain.Tests;

public class VendorAccessTests
{
    private static readonly string[] CardEmails = ["accounts@abc.example"];

    private static string CodeOf(Action act) => Assert.Throws<DomainRuleException>(act).Code;

    private static (VendorInvitation Invitation, string Token) Invite(string email = "accounts@abc.example", ContactConfirmation how = ContactConfirmation.VendorCardEmail, string? note = null) =>
        VendorInvitation.Create("v0001", "ABC Ltd", email, how, note, CardEmails, TimeSpan.FromDays(7), At(Procurement));

    [Fact]
    public void Only_procurement_can_invite_and_accounts_payable_never_can()
    {
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => VendorInvitation.Create("V0001", "ABC Ltd", "accounts@abc.example", ContactConfirmation.VendorCardEmail, null, CardEmails, TimeSpan.FromDays(7), At(AccountsPayable))));

        var both = Actor.Create("pr-ap", "Both roles", VimRole.Procurement, VimRole.AccountsPayable);
        Assert.Equal(RuleCodes.SegregationOfDuties, CodeOf(() => VendorInvitation.Create("V0001", "ABC Ltd", "accounts@abc.example", ContactConfirmation.VendorCardEmail, null, CardEmails, TimeSpan.FromDays(7), At(both))));
    }

    [Fact]
    public void An_email_not_on_the_vendor_card_needs_a_recorded_call_back()
    {
        Assert.Equal(RuleCodes.InvalidValue, CodeOf(() => Invite("new.person@abc.example")));
        Assert.Equal(RuleCodes.ReasonRequired, CodeOf(() => Invite("new.person@abc.example", ContactConfirmation.PhoneCallBack, "called")));

        var (invitation, _) = Invite("new.person@abc.example", ContactConfirmation.PhoneCallBack, "Spoke to Mr Ade on 0803 000 0000 (on file), 24 Oct");
        Assert.Equal("new.person@abc.example", invitation.Email);
        Assert.Equal("V0001", invitation.VendorNo);
    }

    [Fact]
    public void Only_the_hash_of_the_link_token_is_stored()
    {
        var (invitation, token) = Invite();
        Assert.True(token.Length >= 40);
        Assert.NotEqual(token, invitation.TokenHash);
        Assert.Equal(VendorInvitation.HashToken(token), invitation.TokenHash);
    }

    [Fact]
    public void Accepting_needs_the_invited_email_and_works_once()
    {
        var (invitation, _) = Invite();
        Assert.Equal(RuleCodes.InvalidValue, CodeOf(() => invitation.Accept("tid:oid-1", "someone.else@abc.example", Monday10am.AddHours(1))));

        invitation.Accept("tid:oid-1", "Accounts@ABC.example", Monday10am.AddHours(1));
        Assert.Equal(RuleCodes.ActionNotAllowedInStatus, CodeOf(() => invitation.Accept("tid:oid-2", "accounts@abc.example", Monday10am.AddHours(2))));
    }

    [Fact]
    public void Expired_or_withdrawn_invitations_cannot_be_accepted()
    {
        var (expired, _) = Invite();
        Assert.Equal(RuleCodes.ActionNotAllowedInStatus, CodeOf(() => expired.Accept("tid:oid-1", "accounts@abc.example", Monday10am.AddDays(8))));

        var (withdrawn, _) = Invite();
        withdrawn.Revoke(At(Procurement));
        Assert.False(withdrawn.IsUsable(Monday10am));
    }

    [Fact]
    public void A_vendor_user_acts_only_for_its_own_vendor()
    {
        var (invitation, _) = Invite();
        invitation.Accept("tid:oid-1", "accounts@abc.example", Monday10am);
        var user = VendorUser.FromInvitation(invitation, "tid:oid-1", Monday10am);
        var actor = user.ToActor();

        Assert.Equal("V0001", actor.VendorNo);
        Assert.True(actor.HasRole(VimRole.Vendor));
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => InvoiceCase.Submit("VIM-2026-000009", SubmissionChannel.Portal, Submission() with { VendorNo = "V0002" }, At(actor))));
        InvoiceCase.Submit("VIM-2026-000010", SubmissionChannel.Portal, Submission(), At(actor));
    }

    [Fact]
    public void A_vendor_cannot_resubmit_another_vendors_invoice()
    {
        var c = Submitted();
        c.ReturnToVendor("GRV is not signed.", At(Actor.System));
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => c.Resubmit(Submission(), At(OtherVendor))));
    }

    [Fact]
    public void Dormant_users_are_detected_and_removal_needs_a_reason()
    {
        var (invitation, _) = Invite();
        invitation.Accept("tid:oid-1", "accounts@abc.example", Monday10am);
        var user = VendorUser.FromInvitation(invitation, "tid:oid-1", Monday10am);

        Assert.False(user.IsDormant(Monday10am.AddDays(89), 90));
        Assert.True(user.IsDormant(Monday10am.AddDays(91), 90));
        Assert.Equal(RuleCodes.RoleNotPermitted, CodeOf(() => user.Disable("Left the company", At(AccountsPayable))));
        Assert.Equal(RuleCodes.ReasonRequired, CodeOf(() => user.Disable("x", At(Procurement))));

        user.Disable("Left the company", At(FinanceManager));
        Assert.True(user.IsDisabled);
    }
}
