using Desicon.Vim.Domain.Cases;

namespace Desicon.Vim.Domain.VendorAccess;

/// <summary>
/// A portal login bound to exactly one Business Central vendor. The binding comes from an
/// accepted invitation; the vendor never types its own vendor number.
/// </summary>
public sealed class VendorUser
{
    private VendorUser()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>"tenantId:objectId" from the External ID sign-in token. Unique.</summary>
    public string ExternalUserId { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string VendorNo { get; private set; } = string.Empty;

    public string VendorName { get; private set; } = string.Empty;

    public Guid InvitationId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? LastSignInAtUtc { get; private set; }

    public DateTimeOffset? DisabledAtUtc { get; private set; }

    public string? DisabledById { get; private set; }

    public string? DisabledReason { get; private set; }

    public bool IsDisabled => DisabledAtUtc is not null;

    /// <summary>No sign-in (or never signed in since joining) for longer than the dormancy limit.</summary>
    public bool IsDormant(DateTimeOffset nowUtc, int dormantDays) =>
        nowUtc - (LastSignInAtUtc ?? CreatedAtUtc) > TimeSpan.FromDays(dormantDays);

    public static VendorUser FromInvitation(VendorInvitation invitation, string externalUserId, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUserId);
        if (invitation.AcceptedByUserId != externalUserId)
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, "Accept the invitation first.");
        }

        return new VendorUser
        {
            Id = Guid.CreateVersion7(nowUtc),
            ExternalUserId = externalUserId,
            Email = invitation.Email,
            VendorNo = invitation.VendorNo,
            VendorName = invitation.VendorName,
            InvitationId = invitation.Id,
            CreatedAtUtc = nowUtc,
            LastSignInAtUtc = nowUtc,
        };
    }

    public void RecordSignIn(DateTimeOffset nowUtc) => LastSignInAtUtc = nowUtc;

    /// <summary>A new invitation, accepted by this same login, restores a removed or dormant user.</summary>
    public void ReactivateFrom(VendorInvitation invitation, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        if (invitation.AcceptedByUserId != ExternalUserId || invitation.VendorNo != VendorNo)
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, "The invitation does not belong to this login and vendor.");
        }

        InvitationId = invitation.Id;
        Email = invitation.Email;
        VendorName = invitation.VendorName;
        DisabledAtUtc = null;
        DisabledById = null;
        DisabledReason = null;
        LastSignInAtUtc = nowUtc;
    }

    /// <summary>Procurement or the Finance Manager removes a user (leaver, suspected compromise, access review).</summary>
    public void Disable(string reason, ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!ctx.Actor.HasAnyRole([VimRole.Procurement, VimRole.FinanceManager]))
        {
            throw new DomainRuleException(RuleCodes.RoleNotPermitted, "Only Procurement or the Finance Manager can remove a vendor user.");
        }

        DisableBy(reason, ctx.Actor.Id, ctx.NowUtc);
    }

    /// <summary>The platform disables a dormant account.</summary>
    public void DisableAsDormant(int dormantDays, DateTimeOffset nowUtc) =>
        DisableBy($"No sign-in for more than {dormantDays} days.", Actor.System.Id, nowUtc);

    /// <summary>The portal identity of this user, as an actor that can act only for its own vendor.</summary>
    public Actor ToActor() =>
        new(ExternalUserId, $"{Email} ({VendorName})", new HashSet<VimRole> { VimRole.Vendor }) { VendorNo = VendorNo };

    private void DisableBy(string reason, string byId, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
        {
            throw new DomainRuleException(RuleCodes.ReasonRequired, "A reason of at least 5 characters is required.");
        }

        if (IsDisabled)
        {
            return;
        }

        DisabledAtUtc = nowUtc;
        DisabledById = byId;
        DisabledReason = reason.Trim();
    }
}

/// <summary>Append-only record of every invitation, acceptance, sign-in refusal and removal.</summary>
public sealed class VendorAccessEvent
{
    private VendorAccessEvent()
    {
    }

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>Invited, InvitationWithdrawn, Accepted, AcceptRefused, UserDisabled, DormantDisabled, SignInRefused.</summary>
    public string Action { get; private set; } = string.Empty;

    public string VendorNo { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string ActorId { get; private set; } = string.Empty;

    public string ActorName { get; private set; } = string.Empty;

    public string? Details { get; private set; }

    public static VendorAccessEvent Create(string action, string vendorNo, string email, Actor actor, DateTimeOffset nowUtc, string? details)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new VendorAccessEvent
        {
            Id = Guid.CreateVersion7(nowUtc),
            OccurredAtUtc = nowUtc,
            Action = action,
            VendorNo = vendorNo,
            Email = email,
            ActorId = actor.Id,
            ActorName = actor.DisplayName,
            Details = details is { Length: > 2000 } ? details[..2000] : details,
        };
    }
}
