using Desicon.Vim.Application.Intake;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.Messages.Item.Move;

namespace Desicon.Vim.Infrastructure.Messaging;

public sealed record MailboxIntakeResult(int Processed, int Failed, int Skipped);

/// <summary>
/// Reads unread messages from the invoice mailbox Inbox (oldest first, 25 per
/// run), hands each one to intake, then marks it read and moves it to the
/// processed folder. A message whose intake fails stays unread in the Inbox and
/// is retried on the next run; intake itself is idempotent by the immutable Graph message id.
/// Requires Graph application permission Mail.ReadWrite (scoped to the mailbox).
/// </summary>
public sealed class GraphMailboxReader(GraphServiceClient graph, IOptions<MailboxOptions> options, ILogger<GraphMailboxReader> logger)
{
    public const int PageSize = 25;

    private const string AuthenticationResultsHeader = "Authentication-Results";

    // Ask Graph for immutable ids: they stay the same when the message is moved, and unlike the
    // internetMessageId they are assigned by Exchange, not chosen by the sender.
    private const string PreferHeader = "Prefer";
    private const string ImmutableIdPreference = "IdType=\"ImmutableId\"";

    private static readonly string[] MessageSelect =
        ["id", "internetMessageId", "from", "subject", "receivedDateTime", "hasAttachments", "internetMessageHeaders"];

    private static readonly string[] OldestFirst = ["receivedDateTime asc"];

    private string? _processedFolderId;

    /// <param name="processEmail">Called once per message (the caller creates a fresh DI scope and runs IntakeService).</param>
    public async Task<MailboxIntakeResult> ProcessUnreadAsync(Func<InboundEmail, CancellationToken, Task> processEmail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processEmail);
        var userId = options.Value.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            logger.LogWarning("Mailbox:UserId is not configured; mailbox intake is idle.");
            return new MailboxIntakeResult(0, 0, 0);
        }

        // Graph requires an $orderby property to also appear first in $filter, hence the receivedDateTime clause.
        var page = await graph.Users[userId].MailFolders["Inbox"].Messages
            .GetAsync(
                r =>
                {
                    r.Headers.Add(PreferHeader, ImmutableIdPreference);
                    r.QueryParameters.Filter = "receivedDateTime ge 1900-01-01T00:00:00Z and isRead eq false";
                    r.QueryParameters.Orderby = OldestFirst;
                    r.QueryParameters.Top = PageSize;
                    r.QueryParameters.Select = MessageSelect;
                },
                ct)
            .ConfigureAwait(false);

        int processed = 0, failed = 0, skipped = 0;
        foreach (var message in page?.Value ?? [])
        {
            if (string.IsNullOrEmpty(message.Id))
            {
                continue;
            }

            try
            {
                var fromAddress = message.From?.EmailAddress?.Address;
                if (string.IsNullOrWhiteSpace(fromAddress))
                {
                    // Nothing to acknowledge or reply to; move it aside so it does not block the queue.
                    logger.LogWarning("Message {GraphId} has no sender address; moved to the processed folder without intake.", message.Id);
                    await MarkProcessedAsync(userId, message.Id, ct).ConfigureAwait(false);
                    skipped++;
                    continue;
                }

                var email = new InboundEmail(
                    // Idempotency key: the immutable Graph id (the sender controls internetMessageId).
                    MessageId: message.Id,
                    FromAddress: fromAddress,
                    FromName: message.From?.EmailAddress?.Name,
                    Subject: message.Subject,
                    ReceivedAtUtc: message.ReceivedDateTime ?? DateTimeOffset.UtcNow,
                    SenderAuthenticated: ParseSenderAuthenticated(message.InternetMessageHeaders),
                    Attachments: await GetAttachmentsAsync(userId, message.Id, message.HasAttachments == true, ct).ConfigureAwait(false));

                await processEmail(email, ct).ConfigureAwait(false);
                await MarkProcessedAsync(userId, message.Id, ct).ConfigureAwait(false);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad message must not stop the rest; it stays unread and is retried next run.
                failed++;
                logger.LogError(
                    ex,
                    "Mailbox intake failed for message {GraphId} (internet message id {InternetMessageId}).",
                    message.Id,
                    message.InternetMessageId);
            }
        }

        return new MailboxIntakeResult(processed, failed, skipped);
    }

    /// <summary>Overload for Graph headers. See <see cref="ParseSenderAuthenticated(IEnumerable{string})"/>.</summary>
    public static bool? ParseSenderAuthenticated(IEnumerable<InternetMessageHeader>? headers) =>
        headers is null
            ? null
            : ParseSenderAuthenticated(headers
                .Where(h => string.Equals(h.Name, AuthenticationResultsHeader, StringComparison.OrdinalIgnoreCase))
                .Select(h => h.Value ?? string.Empty));

    /// <summary>
    /// Reads Exchange Online's Authentication-Results header values:
    /// false if DMARC or SPF failed (possible spoof), true if DMARC passed,
    /// null if the header is missing or inconclusive.
    /// </summary>
    public static bool? ParseSenderAuthenticated(IEnumerable<string> authenticationResults)
    {
        ArgumentNullException.ThrowIfNull(authenticationResults);
        var all = string.Join(";", authenticationResults);
        if (all.Length == 0)
        {
            return null;
        }

        if (all.Contains("dmarc=fail", StringComparison.OrdinalIgnoreCase)
            || all.Contains("spf=fail", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return all.Contains("dmarc=pass", StringComparison.OrdinalIgnoreCase) ? true : null;
    }

    private async Task<IReadOnlyList<InboundAttachment>> GetAttachmentsAsync(string userId, string graphMessageId, bool hasAttachments, CancellationToken ct)
    {
        if (!hasAttachments)
        {
            return [];
        }

        var response = await graph.Users[userId].Messages[graphMessageId].Attachments
            .GetAsync(r => r.Headers.Add(PreferHeader, ImmutableIdPreference), ct)
            .ConfigureAwait(false);

        var result = new List<InboundAttachment>();
        foreach (var attachment in response?.Value ?? [])
        {
            // Attached emails (ItemAttachment) and links (ReferenceAttachment) are not invoice files.
            if (attachment is not FileAttachment { ContentBytes: { } bytes } file)
            {
                continue;
            }

            result.Add(new InboundAttachment(
                file.Name ?? "attachment",
                file.ContentType ?? "application/octet-stream",
                bytes.LongLength,
                _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))));
        }

        return result;
    }

    private async Task MarkProcessedAsync(string userId, string graphMessageId, CancellationToken ct)
    {
        await graph.Users[userId].Messages[graphMessageId]
            .PatchAsync(new Message { IsRead = true }, r => r.Headers.Add(PreferHeader, ImmutableIdPreference), ct)
            .ConfigureAwait(false);

        var folderId = await GetProcessedFolderIdAsync(userId, ct).ConfigureAwait(false);
        await graph.Users[userId].Messages[graphMessageId].Move
            .PostAsync(new MovePostRequestBody { DestinationId = folderId }, r => r.Headers.Add(PreferHeader, ImmutableIdPreference), ct)
            .ConfigureAwait(false);
    }

    private async Task<string> GetProcessedFolderIdAsync(string userId, CancellationToken ct)
    {
        if (_processedFolderId is not null)
        {
            return _processedFolderId;
        }

        var name = options.Value.ProcessedFolderName;
        var escaped = name.Replace("'", "''", StringComparison.Ordinal);
        var existing = await graph.Users[userId].MailFolders
            .GetAsync(
                r =>
                {
                    r.QueryParameters.Filter = $"displayName eq '{escaped}'";
                    r.QueryParameters.Top = 1;
                },
                ct)
            .ConfigureAwait(false);

        var folderId = existing?.Value is { Count: > 0 } folders ? folders[0].Id : null;
        if (folderId is null)
        {
            var created = await graph.Users[userId].MailFolders
                .PostAsync(new MailFolder { DisplayName = name }, cancellationToken: ct)
                .ConfigureAwait(false);
            folderId = created?.Id ?? throw new InvalidOperationException($"Could not create mail folder '{name}'.");
            logger.LogInformation("Created mail folder {Folder} in the invoice mailbox.", name);
        }

        _processedFolderId = folderId;
        return folderId;
    }
}
