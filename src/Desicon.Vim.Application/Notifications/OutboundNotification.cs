using System.Globalization;
using System.Net;
using Desicon.Vim.Domain.Cases;

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
        $"Received: {c.Reference} — your invoice submission",
        $"<p>Dear Vendor,</p><p>We have received your invoice submission and registered it under reference <strong>{E(c.Reference)}</strong>.</p>" +
        "<p>Our team will check that the invoice and supporting documents are complete. If anything is missing, we will write to you with the reason. Please quote this reference in all correspondence and resubmissions.</p>" +
        "<p><strong>Important:</strong> Desicon pays only into the bank account held on your vendor record. We will never ask you to change bank details by email.</p>" + Footer);

    public static OutboundNotification NonPdfRejected(string to, string? subject) => new(
        Guid.Empty, nameof(NonPdfRejected), NotificationChannel.Email, to,
        "Action needed: invoice submission not accepted (PDF required)",
        $"<p>Dear Vendor,</p><p>Your email \"{E(subject)}\" did not contain a PDF attachment, so it could not be registered.</p>" +
        "<p>Please scan the invoice and all supporting documents into a clear, legible PDF (not a photo) and resend, using the subject format: <em>VendorName_InvoiceNo_PO/SO No_Date</em>.</p>" + Footer);

    public static OutboundNotification ReturnedToVendor(InvoiceCase c, string to, string reason) => new(
        c.Id, nameof(ReturnedToVendor), NotificationChannel.Email, to,
        $"Action needed: {c.Reference} returned for correction",
        $"<p>Dear Vendor,</p><p>Your submission <strong>{E(c.Reference)}</strong> (invoice {E(c.InvoiceNumber)}) cannot be processed yet.</p>" +
        $"<p><strong>Reason:</strong> {E(reason)}</p>" +
        $"<p>Please reply to this email with the corrected documents as one PDF, keeping <strong>{E(c.Reference)}</strong> in the subject line so we can link it to your original submission.</p>" + Footer);

    public static OutboundNotification AssignedToReviewer(InvoiceCase c, string to, string workbenchUrl) => new(
        c.Id, nameof(AssignedToReviewer), NotificationChannel.Email, to,
        $"For review: {c.Reference} — {c.VendorName} — {c.InvoiceNumber}",
        $"<p>A vendor invoice has been assigned to you for verification.</p>" +
        $"<p><strong>{E(c.Reference)}</strong><br/>Vendor: {E(c.VendorName)}<br/>Invoice: {E(c.InvoiceNumber)}<br/>PO/SO: {E(c.OrderNumber)}<br/>Amount: {E(c.Currency)} {c.Amount?.ToString("N2", CultureInfo.InvariantCulture)}<br/>Due: {Due(c)}</p>" +
        $"<p><a href=\"{E(workbenchUrl)}/cases/{c.Id}\">Open in the workbench</a></p>");

    public static OutboundNotification Reminder(InvoiceCase c, string to, string workbenchUrl) => new(
        c.Id, nameof(Reminder), NotificationChannel.Email, to,
        $"Reminder: {c.Reference} due {Due(c)}",
        $"<p>Invoice <strong>{E(c.Reference)}</strong> from {E(c.VendorName)} is awaiting your verification and is due {Due(c)}.</p>" +
        $"<p><a href=\"{E(workbenchUrl)}/cases/{c.Id}\">Open in the workbench</a></p>");

    public static OutboundNotification Escalation(InvoiceCase c, string to, string workbenchUrl) => new(
        c.Id, nameof(Escalation), NotificationChannel.Email, to,
        $"Overdue: {c.Reference} with {c.AssignedReviewerName}",
        $"<p>Invoice <strong>{E(c.Reference)}</strong> from {E(c.VendorName)} passed its verification deadline ({Due(c)}). It is assigned to {E(c.AssignedReviewerName)}.</p>" +
        $"<p><a href=\"{E(workbenchUrl)}/cases/{c.Id}\">Open in the workbench</a></p>");

    public static OutboundNotification ReadyForAccountsPayable(InvoiceCase c, string to, string workbenchUrl) => new(
        c.Id, nameof(ReadyForAccountsPayable), NotificationChannel.Email, to,
        $"Verified: {c.Reference} — {c.VendorName} — ready for AP",
        $"<p>Invoice <strong>{E(c.Reference)}</strong> has been verified and is ready for Accounts Payable processing.</p>" +
        $"<p><a href=\"{E(workbenchUrl)}/cases/{c.Id}\">Open in the workbench</a></p>");

    private static string Due(InvoiceCase c) =>
        c.DueAtUtc is { } d
            ? d.ToOffset(TimeSpan.FromHours(1)).ToString("ddd d MMM yyyy HH:mm 'WAT'", CultureInfo.InvariantCulture)
            : "—";
}
