using System.Text.RegularExpressions;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;

namespace Desicon.Vim.Infrastructure.Messaging;

public sealed record OutboxDispatchResult(int Sent, int Failed);

/// <summary>
/// Sends due outbox messages from the invoice mailbox via Microsoft Graph
/// (Mail.Send application permission, ideally limited to that mailbox with an
/// Exchange application access policy / RBAC for Applications).
/// Failed sends are retried with exponential backoff, up to <see cref="MaxAttempts"/> times.
/// Runs from a timer function, which the Functions host runs as a singleton,
/// so two dispatchers never pick up the same message.
/// </summary>
public sealed partial class OutboxDispatcher(
    VimDbContext db,
    GraphServiceClient graph,
    IClock clock,
    IOptions<MailboxOptions> mailbox,
    ILogger<OutboxDispatcher> logger)
{
    public const int BatchSize = 50;
    public const int MaxAttempts = 8;

    /// <summary>From this failed attempt on, each failure is logged for the operational alert.</summary>
    public const int AlertAfterAttempts = 2;

    private static readonly string[] UserSelect = ["mail", "userPrincipalName"];

    public async Task<OutboxDispatchResult> DispatchAsync(CancellationToken ct)
    {
        var senderMailbox = mailbox.Value.UserId;
        if (string.IsNullOrWhiteSpace(senderMailbox))
        {
            logger.LogWarning("Mailbox:UserId is not configured; outbox messages are not being sent.");
            return new OutboxDispatchResult(0, 0);
        }

        var now = clock.UtcNow;
        var due = await db.OutboxMessages
            .Where(m => m.SentAtUtc == null && m.Attempts < MaxAttempts && m.NextAttemptAtUtc <= now)
            .OrderBy(m => m.CreatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Reviewer addresses are Entra object ids; resolve each one once per run.
        var addressCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int sent = 0, failed = 0;

        foreach (var message in due)
        {
            message.Attempts++;
            try
            {
                if (message.Channel != NotificationChannel.Email)
                {
                    // Permanent: do not retry.
                    message.Attempts = MaxAttempts;
                    throw new NotSupportedException($"Channel {message.Channel} is not implemented yet.");
                }

                var to = await ResolveAddressAsync(message.ToAddress, addressCache, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"No mailbox address found for Entra user {message.ToAddress}.");

                await SendAsync(senderMailbox, to, message, ct).ConfigureAwait(false);
                message.SentAtUtc = clock.UtcNow;
                if (CarriesSecretLink(message))
                {
                    // The one-time link is no longer needed once delivered; never keep a usable copy.
                    message.HtmlBody = InviteLink().Replace(message.HtmlBody, "/invite/[sent]");
                }
                message.LastError = null;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                message.NextAttemptAtUtc = clock.UtcNow + Backoff(message.Attempts);
                logger.LogWarning(
                    ex,
                    "Outbox message {MessageId} ({Template}) attempt {Attempt} failed.",
                    message.Id,
                    message.Template,
                    message.Attempts);

                // Second failure or giving up: raise the "notification emails failing" alert
                // (Azure Monitor matches this exact prefix; logged without the exception so it lands in traces).
                if (message.Attempts >= AlertAfterAttempts)
                {
                    logger.LogError(
                        "VIM_EMAIL_FAILURE {Template} for case {CaseId} to {ToAddress} failed {Attempt} time(s){GaveUp}: {Error}",
                        message.Template,
                        message.CaseId,
                        message.ToAddress,
                        message.Attempts,
                        message.Attempts >= MaxAttempts ? " and will not be retried" : string.Empty,
                        message.LastError);
                }
            }

            // Save after each message so a crash mid-batch does not resend what already went out.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (due.Count > 0)
        {
            logger.LogInformation("Outbox dispatch: {Sent} sent, {Failed} failed.", sent, failed);
        }

        return new OutboxDispatchResult(sent, failed);
    }

    /// <summary>1, 2, 4, 8... minutes, capped at 6 hours.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Max(0, attempts - 1)), 360));

    private async Task<string?> ResolveAddressAsync(string toAddress, Dictionary<string, string?> cache, CancellationToken ct)
    {
        if (!Guid.TryParse(toAddress, out _))
        {
            return toAddress;
        }

        if (cache.TryGetValue(toAddress, out var cached))
        {
            return cached;
        }

        var user = await graph.Users[toAddress]
            .GetAsync(r => r.QueryParameters.Select = UserSelect, ct)
            .ConfigureAwait(false);
        var mail = user?.Mail;
        var address = !string.IsNullOrWhiteSpace(mail) ? mail : user?.UserPrincipalName;
        cache[toAddress] = address;
        return address;
    }

    private async Task SendAsync(string senderMailbox, string to, OutboxMessage message, CancellationToken ct)
    {
        var body = new SendMailPostRequestBody
        {
            Message = new Message
            {
                Subject = message.Subject,
                Body = new ItemBody { ContentType = BodyType.Html, Content = message.HtmlBody },
                ToRecipients =
                [
                    new Recipient { EmailAddress = new EmailAddress { Address = to } },
                ],
            },
            // Invitation emails carry a one-time sign-up link: no copy in the shared mailbox.
            SaveToSentItems = !CarriesSecretLink(message),
        };

        await graph.Users[senderMailbox].SendMail.PostAsync(body, cancellationToken: ct).ConfigureAwait(false);
    }

    private static bool CarriesSecretLink(OutboxMessage message) =>
        string.Equals(message.Template, nameof(NotificationTemplates.PortalInvitation), StringComparison.Ordinal);

    [GeneratedRegex("/invite/[A-Za-z0-9_-]+")]
    private static partial Regex InviteLink();
}
