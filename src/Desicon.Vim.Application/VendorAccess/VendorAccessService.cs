using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Cases;
using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.VendorAccess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Application.VendorAccess;

/// <summary>Who signed in to the portal, as the External ID token states it.</summary>
/// <param name="ExternalUserId">"tenantId:objectId".</param>
/// <param name="Email">The sign-in email. External ID verifies it with a one-time passcode.</param>
public sealed record PortalSignIn(string ExternalUserId, string Email);

/// <summary>What the portal may do for a signed-in person.</summary>
public enum PortalAccess
{
    /// <summary>Linked to an active vendor; may use the portal.</summary>
    Allowed = 0,

    /// <summary>Signed in but never invited (or invitation not yet accepted).</summary>
    NotLinked = 1,

    /// <summary>Removed by Procurement / the Finance Manager, or disabled as dormant.</summary>
    Disabled = 2,

    /// <summary>The vendor is blocked (or missing) in Business Central.</summary>
    VendorBlocked = 3,
}

public sealed record PortalAccessResult(PortalAccess Access, VendorUser? User, string Message);

/// <summary>
/// Vendor portal access (phase 1): Procurement invites a confirmed contact for a Business
/// Central vendor; the person signs in to the portal with that email and accepts; their
/// login is then bound to that vendor and nothing else. Every step is logged.
/// </summary>
public sealed class VendorAccessService(
    IVendorAccessRepository access,
    IErpGateway erp,
    INotificationOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<VimOptions> options,
    ILogger<VendorAccessService> logger)
{
    /// <summary>Procurement invites a contact. Returns the invitation (the token goes only into the email).</summary>
    public async Task<VendorInvitation> InviteAsync(Actor actor, string vendorNo, string email, ContactConfirmation confirmation, string? confirmationNote, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        VendorInvitation.EnsureCanInvite(actor);
        var o = options.Value;
        if (!Uri.TryCreate(o.PortalUrl, UriKind.Absolute, out _))
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, "The vendor portal address is not configured yet (Vim:PortalUrl).");
        }

        if (!erp.IsEnabled)
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, "Business Central must be connected to check the vendor before inviting.");
        }

        var number = (vendorNo ?? string.Empty).Trim().ToUpperInvariant();
        var vendor = await erp.GetVendorAsync(number, ct).ConfigureAwait(false)
            ?? throw new DomainRuleException(RuleCodes.InvalidValue, $"Vendor {number} was not found in Business Central.");
        if (vendor.Blocked)
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, $"Vendor {number} is blocked in Business Central and cannot be invited.");
        }

        var ctx = ActionContext.For(actor, clock.UtcNow);
        var (invitation, token) = VendorInvitation.Create(
            vendor.VendorNo, vendor.Name, email, confirmation, confirmationNote, vendor.Emails,
            TimeSpan.FromDays(o.InvitationValidityDays), ctx);

        // A new invitation replaces any still-pending one for the same person and vendor.
        foreach (var old in (await access.ListPendingInvitationsAsync(ctx.NowUtc, invitation.VendorNo, ct).ConfigureAwait(false))
                     .Where(i => i.Email == invitation.Email))
        {
            old.Revoke(ctx);
            access.AddEvent(VendorAccessEvent.Create("InvitationWithdrawn", old.VendorNo, old.Email, actor, ctx.NowUtc, "Replaced by a new invitation."));
        }

        access.AddInvitation(invitation);
        access.AddEvent(VendorAccessEvent.Create("Invited", invitation.VendorNo, invitation.Email, actor, ctx.NowUtc,
            confirmation == ContactConfirmation.VendorCardEmail ? "Email on BC vendor card." : "Call-back: " + invitation.ConfirmationNote));

        var link = $"{o.PortalUrl.TrimEnd('/')}/invite/{token}";
        outbox.Enqueue(NotificationTemplates.PortalInvitation(invitation, link));
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return invitation;
    }

    public async Task WithdrawInvitationAsync(Guid invitationId, Actor actor, CancellationToken ct)
    {
        var invitation = await access.GetInvitationAsync(invitationId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Invitation not found.");
        var ctx = ActionContext.For(actor, clock.UtcNow);
        invitation.Revoke(ctx);
        access.AddEvent(VendorAccessEvent.Create("InvitationWithdrawn", invitation.VendorNo, invitation.Email, actor, ctx.NowUtc, null));
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DisableUserAsync(Guid userId, string reason, Actor actor, CancellationToken ct)
    {
        var user = await access.GetUserAsync(userId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Vendor user not found.");
        var ctx = ActionContext.For(actor, clock.UtcNow);
        user.Disable(reason, ctx);
        access.AddEvent(VendorAccessEvent.Create("UserDisabled", user.VendorNo, user.Email, actor, ctx.NowUtc, reason));
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The person opened their invitation link and signed in. Binds the login to the vendor
    /// if the invitation is valid, the sign-in email is the invited one and the vendor is
    /// still active in Business Central.
    /// </summary>
    public async Task<PortalAccessResult> AcceptInvitationAsync(string token, PortalSignIn signIn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        var now = clock.UtcNow;
        var invitation = string.IsNullOrWhiteSpace(token) ? null
            : await access.GetInvitationByTokenHashAsync(VendorInvitation.HashToken(token.Trim()), ct).ConfigureAwait(false);
        if (invitation is null)
        {
            return new PortalAccessResult(PortalAccess.NotLinked, null, "This invitation link is not valid. Ask Desicon Procurement for a new invitation.");
        }

        var actor = new Actor(signIn.ExternalUserId, signIn.Email, new HashSet<VimRole> { VimRole.Vendor });
        var existing = await access.GetUserByExternalIdAsync(signIn.ExternalUserId, ct).ConfigureAwait(false);
        if (existing is not null && !string.Equals(existing.VendorNo, invitation.VendorNo, StringComparison.Ordinal))
        {
            access.AddEvent(VendorAccessEvent.Create("AcceptRefused", invitation.VendorNo, invitation.Email, actor, now, $"Login already linked to {existing.VendorNo}."));
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new PortalAccessResult(PortalAccess.NotLinked, null,
                "This sign-in is already linked to another vendor. Each login works for one vendor only; use a separate email address for this vendor.");
        }

        var vendor = await erp.GetVendorAsync(invitation.VendorNo, ct).ConfigureAwait(false);
        if (vendor is null || vendor.Blocked)
        {
            access.AddEvent(VendorAccessEvent.Create("AcceptRefused", invitation.VendorNo, invitation.Email, actor, now, "Vendor blocked or missing in BC."));
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new PortalAccessResult(PortalAccess.VendorBlocked, null, "Your vendor account is on hold. Please contact Desicon Procurement.");
        }

        try
        {
            invitation.Accept(signIn.ExternalUserId, signIn.Email, now);
        }
        catch (DomainRuleException ex)
        {
            access.AddEvent(VendorAccessEvent.Create("AcceptRefused", invitation.VendorNo, invitation.Email, actor, now, ex.Message));
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new PortalAccessResult(PortalAccess.NotLinked, null, ex.Message);
        }

        var user = existing ?? VendorUser.FromInvitation(invitation, signIn.ExternalUserId, now);
        if (existing is null)
        {
            access.AddUser(user);
        }
        else
        {
            // Procurement re-invited this person: a new invitation restores a removed or dormant login.
            user.ReactivateFrom(invitation, now);
        }

        access.AddEvent(VendorAccessEvent.Create("Accepted", invitation.VendorNo, invitation.Email, actor, now, existing is null ? null : "Existing login re-activated."));
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Vendor portal invitation accepted for vendor {VendorNo}.", invitation.VendorNo);
        return new PortalAccessResult(PortalAccess.Allowed, user, $"Your sign-in is now linked to {user.VendorName}.");
    }

    /// <summary>
    /// Every portal request starts here: is this login linked, active and its vendor not
    /// blocked? Dormant accounts are disabled on the spot. Sign-in time is recorded at most
    /// once an hour.
    /// </summary>
    public async Task<PortalAccessResult> ResolveAsync(PortalSignIn signIn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        var now = clock.UtcNow;
        var user = await access.GetUserByExternalIdAsync(signIn.ExternalUserId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return new PortalAccessResult(PortalAccess.NotLinked, null,
                "Your sign-in is not linked to a Desicon vendor yet. Open the invitation link Desicon Procurement sent you, or ask them for one.");
        }

        if (user.IsDisabled)
        {
            return new PortalAccessResult(PortalAccess.Disabled, user, "Your portal access has been removed. Please contact Desicon Procurement.");
        }

        var dormantDays = options.Value.VendorDormantDays;
        if (user.IsDormant(now, dormantDays))
        {
            user.DisableAsDormant(dormantDays, now);
            access.AddEvent(VendorAccessEvent.Create("DormantDisabled", user.VendorNo, user.Email, Actor.System, now, null));
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new PortalAccessResult(PortalAccess.Disabled, user,
                $"Your portal access was paused because it was not used for {dormantDays} days. Please contact Desicon Procurement.");
        }

        var vendor = await erp.GetVendorAsync(user.VendorNo, ct).ConfigureAwait(false);
        if (vendor is null || vendor.Blocked)
        {
            access.AddEvent(VendorAccessEvent.Create("SignInRefused", user.VendorNo, user.Email, user.ToActor(), now, "Vendor blocked or missing in BC."));
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new PortalAccessResult(PortalAccess.VendorBlocked, user, "Your vendor account is on hold. Please contact Desicon Procurement.");
        }

        if (user.LastSignInAtUtc is not { } last || now - last > TimeSpan.FromHours(1))
        {
            user.RecordSignIn(now);
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new PortalAccessResult(PortalAccess.Allowed, user, string.Empty);
    }

    /// <summary>For the workbench: users, pending invitations and recent activity.</summary>
    public async Task<VendorAccessOverview> GetOverviewAsync(string? vendorNo, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var filter = string.IsNullOrWhiteSpace(vendorNo) ? null : vendorNo.Trim().ToUpperInvariant();
        var users = await access.ListUsersAsync(filter, ct).ConfigureAwait(false);
        var pending = await access.ListPendingInvitationsAsync(now, filter, ct).ConfigureAwait(false);
        var events = await access.ListEventsAsync(filter, 50, ct).ConfigureAwait(false);
        return new VendorAccessOverview(users, pending, events, now, options.Value.VendorDormantDays);
    }
}

public sealed record VendorAccessOverview(
    IReadOnlyList<VendorUser> Users,
    IReadOnlyList<VendorInvitation> PendingInvitations,
    IReadOnlyList<VendorAccessEvent> RecentEvents,
    DateTimeOffset NowUtc,
    int DormantDays);
