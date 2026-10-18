using System.Security.Cryptography;
using System.Text;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Domain.Integrity;
using Desicon.Vim.Domain.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Vim.Infrastructure.Persistence;

/// <summary>
/// Issues VIM-yyyy-nnnnnn references from the SQL SEQUENCE vim.CaseReferenceSeq.
/// The sequence is global (it does not restart each year), so numbers never
/// repeat even across years; the year in the reference is the receipt year.
/// </summary>
public sealed class SqlSequenceReferenceGenerator(VimDbContext db) : IReferenceGenerator
{
    // NEXT VALUE FOR is not allowed inside a derived table, so this query must not be composed
    // (no First/Single on the IQueryable) — materialise it as-is.
    private const string NextValueSql =
        "SELECT NEXT VALUE FOR [" + VimDbContext.Schema + "].[" + VimDbContext.CaseReferenceSequence + "] AS [Value]";

    public async Task<string> NextAsync(int year, CancellationToken ct)
    {
        var values = await db.Database.SqlQueryRaw<long>(NextValueSql).ToListAsync(ct).ConfigureAwait(false);
        return CaseReference.Format(year, values.Single());
    }
}

public sealed class EfRouteRepository(VimDbContext db) : IRouteRepository
{
    public Task<CategoryRoute?> GetAsync(string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalised = code.Trim().ToUpperInvariant();
        return db.CategoryRoutes.SingleOrDefaultAsync(r => r.Code == normalised, ct);
    }

    public async Task<IReadOnlyList<CategoryRoute>> ListAsync(CancellationToken ct) =>
        await db.CategoryRoutes.OrderBy(r => r.Code).ToListAsync(ct).ConfigureAwait(false);

    public void Add(CategoryRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        db.CategoryRoutes.Add(route);
    }

    /// <summary>Insert-only: saved with the route change by the same unit of work.</summary>
    public void AddChange(RouteChangeRecord change)
    {
        ArgumentNullException.ThrowIfNull(change);
        db.RouteChanges.Add(change);
    }
}

/// <summary>Idempotency log for inbound emails. Rows are saved by the same unit of work as the case.</summary>
public sealed class EfInboundMessageLog(VimDbContext db) : IInboundMessageLog
{
    /// <summary>nvarchar(450) keeps the primary key within SQL Server's 900-byte index key limit.</summary>
    public const int MaxKeyLength = 450;

    public async Task<bool> HasProcessedAsync(string messageId, CancellationToken ct)
    {
        var key = ToKey(messageId);
        if (db.InboundMessages.Local.Any(m => m.MessageId == key))
        {
            return true;
        }

        return await db.InboundMessages.AnyAsync(m => m.MessageId == key, ct).ConfigureAwait(false);
    }

    public void MarkProcessed(string messageId, string? caseReference, string outcome, DateTimeOffset atUtc)
    {
        db.InboundMessages.Add(new InboundMessage
        {
            MessageId = ToKey(messageId),
            CaseReference = caseReference,
            Outcome = outcome,
            ProcessedAtUtc = atUtc,
        });
    }

    /// <summary>Internet message ids are normally short; an unusually long one is replaced by its SHA-256.</summary>
    internal static string ToKey(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return messageId.Length <= MaxKeyLength
            ? messageId
            : "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId)));
    }
}

/// <summary>Adds notifications to the outbox table; the unit of work saves them with the case change.</summary>
public sealed class EfNotificationOutbox(VimDbContext db, IClock clock) : INotificationOutbox
{
    public void Enqueue(OutboundNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var now = clock.UtcNow;
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            CaseId = notification.CaseId,
            Template = notification.Template,
            Channel = notification.Channel,
            ToAddress = notification.ToAddress,
            Subject = notification.Subject,
            HtmlBody = notification.HtmlBody,
            CreatedAtUtc = now,
            Attempts = 0,
            NextAttemptAtUtc = now,
        });
    }
}
