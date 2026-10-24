using System.Globalization;
using System.Net;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.VendorAccess;

namespace Desicon.Vim.Application.Notifications;

public enum NotificationChannel
{
    Email = 0,
    Teams = 1,
}

/// <summary>
/// A message waiting to be sent. Saved in the same transaction as the case
/// change (the "outbox"), then sent by a background function — so a mail
/// outage never loses a notification or rolls back a decision.
/// </summary>
public sealed record OutboundNotification(
    Guid CaseId,
    string Template,
    NotificationChannel Channel,
    string ToAddress,
    string Subject,
    string HtmlBody);

/// <summary>Plain, professional wording for each message. Bodies are HTML-encoded to prevent injection from vendor-supplied text.</summary>
public static class NotificationTemplates
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    private static string Footer =>
        "<p style=\"color:#666;font-size:12px\">Desicon Engineering Limited — Accounts Payable. This is an automated message from the Vendor Invoice Management Platform. Please quote the reference in any reply.</p>";

    public static OutboundNotification Acknowledgement(InvoiceCase c, string to) => new(
        c.Id, nameof(Acknowledgement), NotificationChannel.Email, to,
        $"Received: {c.Reference} — invoice {c.InvoiceNumber}",
        $"<p>Dear {E(c.VendorName ?? "Vendor")},</p><p>We have received your invoice submission{Round(c)} and recorded it under reference <strong>{E(c.Reference)}</strong>{InvoiceLine(c)}.</p>" +
        "<p>It will now be checked automatically against your purchase / service order and the delivery confirmed by our department, and then by Accounts. " +
        "We will write to you when it is <strong>accepted</strong> (your credit period counts from the acceptance date) or, if anything needs correcting, with the reasons.</p>" +
        "<p><strong>Important:</strong> Desicon pays only into the bank account held on your vendor record. We will never ask you to change bank details by email.</p>" + Footer);

    public static OutboundNotification NonPdfRejected(string to, string? subject) => new(
        Guid.Empty, nameof(NonPdfRejected), NotificationChannel.Email, to,
        "Action needed: invoice submission not accepted (PDF required)",
        $"<p>Dear Vendor,</p><p>Your email \"{E(subject)}\" did not contain a PDF attachment, so it could not be registered.</p>" +
        "<p>Please scan the invoice and all supporting documents into a clear, legible PDF (not a photo) and resend, using the subject format: <em>VendorName_InvoiceNo_PO/SO No_Date</em>.</p>" + Footer);

    public static OutboundNotification ReturnedToVendor(InvoiceCase c, string to, string reason) => new(
        c.Id, nameof(ReturnedToVendor), NotificationChannel.Email, to,
        $"Not accepted: {c.Reference} — invoice {c.InvoiceNumber} needs correction",
        $"<p>Dear {E(c.VendorName ?? "Vendor")},</p><p>Your submission <strong>{E(c.Reference)}</strong>{InvoiceLine(c)} has <strong>not been accepted</strong>, so no credit period is running on it.</p>" +
        $"<p><strong>Reason:</strong> {E(reason)}</p>" +
        $"<p>Please correct the points above and resubmit against reference <strong>{E(c.Reference)}</strong>. If you need help, contact Accounts Payable quoting this reference.</p>" + Footer);

    public static OutboundNotification Accepted(InvoiceCase c, string to) => new(
        c.Id, nameof(Accepted), NotificationChannel.Email, to,
        $"Accepted: {c.Reference} — invoice {c.InvoiceNumber}",
        $"<p>Dear {E(c.VendorName ?? "Vendor")},</p><p>Your invoice submission <strong>{E(c.Reference)}</strong>{InvoiceLine(c)} has been checked and <strong>accepted</strong> by Desicon Accounts on <strong>{Wat(c.AcceptedAtUtc)}</strong>.</p>" +
        "<p>This is the official submission date: your agreed credit period counts from it. Payment will be made to the bank account held on your vendor record.</p>" + Footer);

    public static OutboundNotification ReadyForAccounts(InvoiceCase c, string to, string workbenchUrl) => new(
        c.Id, nameof(ReadyForAccounts), NotificationChannel.Email, to,
        $"For confirmation: {c.Reference} — {c.VendorName} — {c.InvoiceNumber}",
        $"<p>Invoice <strong>{E(c.Reference)}</strong> has passed the automatic checks (PO, posted receipt, full value, duplicates) and is waiting for your confirmatory check.</p>" +
        Summary(c) + $"<p>Due: {Due(c)}</p><p><a href=\"{E(workbenchUrl)}/cases/{c.Id}\">Open in the workbench</a></p>");

    public static OutboundNotification ExceptionForFinanceManager(InvoiceCase c, string to, string workbenchUrl) => new(
        c.Id, nameof(ExceptionForFinanceManager), NotificationChannel.Email, to,
        $"Decision needed: {c.Reference} — {c.VendorName} — {c.InvoiceNumber}",
        $"<p>Invoice <strong>{E(c.Reference)}</strong> needs your decision before it can go to Accounts:</p><ul>" +
        string.Concat(c.Flags.Where(f => f.IsOpen && f.Blocking).Select(f => $"<li>{E(f.Description)}</li>")) + "</ul>" +
        Summary(c) + $"<p>Due: {Due(c)}</p><p><a href=\"{E(workbenchUrl)}/cases/{c.Id}\">Open in the workbench</a></p>");

    public static OutboundNotification Reminder(InvoiceCase c, string to, string workbenchUrl) => new(
        c.Id, nameof(Reminder), NotificationChannel.Email, to,
        $"Reminder: {c.Reference} due {Due(c)}",
        $"<p>Invoice <strong>{E(c.Reference)}</strong> from {E(c.VendorName)} is waiting for {E(c.AssignedReviewerName)} and is due {Due(c)}.</p>" +
        "<p>Desicon's target is acceptance within 3 working days of delivery; delays on our side must not look like delaying payment.</p>" +
        $"<p><a href=\"{E(workbenchUrl)}/cases/{c.Id}\">Open in the workbench</a></p>");

    public static OutboundNotification Escalation(InvoiceCase c, string to, string workbenchUrl) => new(
        c.Id, nameof(Escalation), NotificationChannel.Email, to,
        $"Overdue: {c.Reference} with {c.AssignedReviewerName}",
        $"<p>Invoice <strong>{E(c.Reference)}</strong> from {E(c.VendorName)} passed its deadline ({Due(c)}) while with {E(c.AssignedReviewerName)}.</p>" +
        Summary(c) + $"<p><a href=\"{E(workbenchUrl)}/cases/{c.Id}\">Open in the workbench</a></p>");

    public static OutboundNotification PortalInvitation(VendorInvitation invitation, string link) => new(
        Guid.Empty, nameof(PortalInvitation), NotificationChannel.Email, invitation.Email,
        $"Invitation: Desicon vendor portal for {invitation.VendorName}",
        $"<p>Dear {E(invitation.VendorName)} team,</p><p>Desicon Engineering invites you to submit and track your invoices on the Desicon vendor portal, for vendor account <strong>{E(invitation.VendorNo)}</strong>.</p>" +
        $"<p><a href=\"{E(link)}\">Accept the invitation</a></p>" +
        $"<p>Sign in with <strong>this email address ({E(invitation.Email)})</strong>; you will receive a one-time code to confirm it. The link works once and expires on {Wat(invitation.ExpiresAtUtc)}.</p>" +
        "<p>If you did not expect this invitation, ignore this email and tell Desicon Procurement. Desicon will never ask for passwords or bank details by email.</p>" + Footer);

    private static string Summary(InvoiceCase c) =>
        $"<p>Vendor: {E(c.VendorName)} ({E(c.VendorNo)})<br/>Invoice: {E(c.InvoiceNumber)}<br/>PO/SO: {E(c.OrderNumber)}<br/>Amount: {E(c.Currency)} {c.Amount?.ToString("N2", CultureInfo.InvariantCulture)}</p>";

    private static string InvoiceLine(InvoiceCase c) =>
        c.InvoiceNumber is null ? string.Empty : $" (invoice {E(c.InvoiceNumber)}, {E(c.Currency)} {c.Amount?.ToString("N2", CultureInfo.InvariantCulture)}, PO/SO {E(c.OrderNumber)})";

    private static string Round(InvoiceCase c) => c.SubmissionRound > 1 ? " (corrected)" : string.Empty;

    private static string Wat(DateTimeOffset? utc) =>
        utc is { } d ? d.ToOffset(TimeSpan.FromHours(1)).ToString("ddd d MMM yyyy HH:mm 'WAT'", CultureInfo.InvariantCulture) : "—";

    private static string Due(InvoiceCase c) =>
        c.DueAtUtc is { } d
            ? d.ToOffset(TimeSpan.FromHours(1)).ToString("ddd d MMM yyyy HH:mm 'WAT'", CultureInfo.InvariantCulture)
            : "—";
}
