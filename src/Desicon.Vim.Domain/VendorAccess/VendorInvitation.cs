using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Desicon.Vim.Domain.Cases;

namespace Desicon.Vim.Domain.VendorAccess;

/// <summary>How Procurement confirmed that the invited person really speaks for the vendor.</summary>
public enum ContactConfirmation
{
    /// <summary>The email is the one already on the Business Central vendor card.</summary>
    VendorCardEmail = 0,

    /// <summary>Procurement phoned the vendor on a number already on file and confirmed the contact.</summary>
    PhoneCallBack = 1,
}

/// <summary>
/// A one-time invitation for a vendor contact to the vendor portal. Only Procurement
/// can issue one (an Accounts Payable officer never can: whoever accepts invoices must
/// not also create the people who submit them). The link carries a random token; only
/// its SHA-256 hash is stored, so a database read never reveals a usable link.
/// </summary>
public sealed class VendorInvitation
{
    public const int MinimumNoteLength = 15;

    private VendorInvitation()
    {
    }

    public Guid Id { get; private set; }

    public string VendorNo { get; private set; } = string.Empty;

    public string VendorName { get; private set; } = string.Empty;

    /// <summary>Lower-cased. The person must sign in with exactly this (verified) email.</summary>
    public string Email { get; private set; } = string.Empty;

    public string TokenHash { get; private set; } = string.Empty;

    public ContactConfirmation Confirmation { get; private set; }

    /// <summary>For a call-back: who was called, on which number on file, and when.</summary>
    public string? ConfirmationNote { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public string CreatedById { get; private set; } = string.Empty;

    public string CreatedByName { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? AcceptedAtUtc { get; private set; }

    public string? AcceptedByUserId { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public string? RevokedById { get; private set; }

    public bool IsUsable(DateTimeOffset nowUtc) => AcceptedAtUtc is null && RevokedAtUtc is null && nowUtc < ExpiresAtUtc;

    public bool IsPending(DateTimeOffset nowUtc) => IsUsable(nowUtc);

    /// <summary>Creates the invitation and returns the token for the link (shown once, never stored).</summary>
    public static (VendorInvitation Invitation, string Token) Create(
        string vendorNo,
        string vendorName,
        string email,
        ContactConfirmation confirmation,
        string? confirmationNote,
        IReadOnlyCollection<string> vendorCardEmails,
        TimeSpan validity,
        ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(vendorCardEmails);
        ArgumentNullException.ThrowIfNull(ctx);
        EnsureCanInvite(ctx.Actor);

        if (string.IsNullOrWhiteSpace(vendorNo) || string.IsNullOrWhiteSpace(vendorName))
        {
            throw new DomainRuleException(RuleCodes.MissingDetails, "Choose the Business Central vendor.");
        }

        var normalised = NormaliseEmail(email);
        switch (confirmation)
        {
            case ContactConfirmation.VendorCardEmail when !vendorCardEmails.Any(e => string.Equals(e.Trim(), normalised, StringComparison.OrdinalIgnoreCase)):
                throw new DomainRuleException(RuleCodes.InvalidValue,
                    "That email is not on the vendor's Business Central card. Confirm the contact by phoning a number already on file, and record the call.");
            case ContactConfirmation.PhoneCallBack when string.IsNullOrWhiteSpace(confirmationNote) || confirmationNote.Trim().Length < MinimumNoteLength:
                throw new DomainRuleException(RuleCodes.ReasonRequired,
                    "Record the call-back: who you spoke to, the number on file you called, and when.");
            default:
                break;
        }

        if (validity <= TimeSpan.Zero || validity > TimeSpan.FromDays(30))
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, "Invitations are valid for 1 to 30 days.");
        }

        var token = NewToken();
        var invitation = new VendorInvitation
        {
            Id = Guid.CreateVersion7(ctx.NowUtc),
            VendorNo = vendorNo.Trim().ToUpperInvariant(),
            VendorName = vendorName.Trim(),
            Email = normalised,
            TokenHash = HashToken(token),
            Confirmation = confirmation,
            ConfirmationNote = confirmation == ContactConfirmation.PhoneCallBack ? confirmationNote!.Trim() : null,
            CreatedAtUtc = ctx.NowUtc,
            CreatedById = ctx.Actor.Id,
            CreatedByName = ctx.Actor.DisplayName,
            ExpiresAtUtc = ctx.NowUtc + validity,
        };
        return (invitation, token);
    }

    /// <summary>
    /// The invited person has signed in with a verified email. The email must be the
    /// invited one: a forwarded link is useless to anyone else.
    /// </summary>
    public void Accept(string externalUserId, string signedInEmail, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUserId);
        if (!IsUsable(nowUtc))
        {
            throw new DomainRuleException(RuleCodes.ActionNotAllowedInStatus,
                "This invitation has expired, was withdrawn, or has already been used. Ask Desicon Procurement for a new one.");
        }

        if (!string.Equals(NormaliseEmail(signedInEmail), Email, StringComparison.Ordinal))
        {
            throw new DomainRuleException(RuleCodes.InvalidValue,
                "This invitation was sent to a different email address. Sign in with the address the invitation was sent to.");
        }

        AcceptedAtUtc = nowUtc;
        AcceptedByUserId = externalUserId;
    }

    public void Revoke(ActionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!ctx.Actor.HasAnyRole([VimRole.Procurement, VimRole.FinanceManager]))
        {
            throw new DomainRuleException(RuleCodes.RoleNotPermitted, "Only Procurement or the Finance Manager can withdraw an invitation.");
        }

        if (AcceptedAtUtc is not null || RevokedAtUtc is not null)
        {
            return;
        }

        RevokedAtUtc = ctx.NowUtc;
        RevokedById = ctx.Actor.Id;
    }

    public static void EnsureCanInvite(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.HasRole(VimRole.Procurement))
        {
            throw new DomainRuleException(RuleCodes.RoleNotPermitted, "Only Procurement can invite vendor contacts to the portal.");
        }

        if (actor.HasRole(VimRole.AccountsPayable))
        {
            throw new DomainRuleException(RuleCodes.SegregationOfDuties,
                "Someone who accepts invoices (Accounts Payable) cannot also invite vendor contacts.");
        }
    }

    public static string HashToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static string NormaliseEmail(string? email)
    {
        var trimmed = email?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > 320 || !MailAddress.TryCreate(trimmed, out var parsed) || !string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainRuleException(RuleCodes.InvalidValue, "Enter a valid email address.");
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>256 random bits, URL-safe.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
