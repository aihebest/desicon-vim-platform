using Desicon.Vim.Application.Notifications;

namespace Desicon.Vim.Infrastructure.Persistence;

/// <summary>
/// A notification waiting to be sent (the "outbox" pattern). Written in the
/// same transaction as the case change; sent later by the OutboxDispatcher.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>Case the message relates to; <see cref="Guid.Empty"/> for messages with no case (e.g. "PDF required").</summary>
    public Guid CaseId { get; set; }

    public string Template { get; set; } = string.Empty;

    public NotificationChannel Channel { get; set; }

    /// <summary>An email address, or an Entra object id (GUID) that the dispatcher resolves to the user's mailbox.</summary>
    public string ToAddress { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string HtmlBody { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAtUtc { get; set; }

    public DateTimeOffset? SentAtUtc { get; set; }

    public string? LastError { get; set; }
}

/// <summary>An inbound email already handled by intake (idempotency record).</summary>
public sealed class InboundMessage
{
    public string MessageId { get; set; } = string.Empty;

    public string? CaseReference { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public DateTimeOffset ProcessedAtUtc { get; set; }
}
