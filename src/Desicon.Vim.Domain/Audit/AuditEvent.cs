using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Desicon.Vim.Domain.Cases;

namespace Desicon.Vim.Domain.Audit;

/// <summary>
/// One entry in a case's history. Insert-only: corrections are new events,
/// never edits. Each event carries the hash of the previous one, so removing
/// or altering an entry breaks the chain and is detectable.
/// </summary>
public sealed class AuditEvent
{
    private AuditEvent()
    {
    }

    public Guid Id { get; private set; }

    public Guid CaseId { get; private set; }

    /// <summary>1, 2, 3... within the case.</summary>
    public int Sequence { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public CaseStatus? FromStatus { get; private set; }

    public CaseStatus? ToStatus { get; private set; }

    public string ActorId { get; private set; } = string.Empty;

    public string ActorName { get; private set; } = string.Empty;

    public string ActorRoles { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>Small JSON document with action-specific details (checklist answers, BC number...).</summary>
    public string? DetailsJson { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public string PreviousHash { get; private set; } = string.Empty;

    public string Hash { get; private set; } = string.Empty;

    public const string GenesisHash = "GENESIS";

    internal static AuditEvent Create(
        Guid caseId,
        int sequence,
        string action,
        CaseStatus? from,
        CaseStatus? to,
        Actor actor,
        DateTimeOffset occurredAtUtc,
        string? reason,
        string? detailsJson,
        string correlationId,
        string previousHash)
    {
        var e = new AuditEvent
        {
            Id = Guid.CreateVersion7(occurredAtUtc),
            CaseId = caseId,
            Sequence = sequence,
            Action = action,
            FromStatus = from,
            ToStatus = to,
            ActorId = actor.Id,
            ActorName = actor.DisplayName,
            ActorRoles = string.Join(',', actor.Roles.Order()),
            OccurredAtUtc = occurredAtUtc.ToUniversalTime(),
            Reason = reason,
            DetailsJson = detailsJson,
            CorrelationId = correlationId,
            PreviousHash = previousHash,
        };
        e.Hash = e.ComputeHash();
        return e;
    }

    /// <summary>Recomputes this event's hash from its content.</summary>
    public string ComputeHash()
    {
        var canonical = string.Join('|',
            Id.ToString("N"),
            CaseId.ToString("N"),
            Sequence.ToString(CultureInfo.InvariantCulture),
            Action,
            FromStatus?.ToString() ?? "-",
            ToStatus?.ToString() ?? "-",
            ActorId,
            ActorName,
            ActorRoles,
            CorrelationId,
            OccurredAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            Reason ?? string.Empty,
            DetailsJson ?? string.Empty,
            PreviousHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Checks an ordered history: sequence has no gaps, each event links to the
    /// one before it, and no event's content was changed. Pass the case's
    /// <c>LastAuditHash</c> as <paramref name="expectedHead"/> to also detect
    /// events removed from the end.
    /// </summary>
    /// <remarks>
    /// A plain hash chain proves internal consistency; it does not stop someone
    /// with database write access from rewriting the whole chain. That is
    /// covered by DENY UPDATE/DELETE on the table and, as a later hardening
    /// step, anchoring the head hash outside the database (see backlog).
    /// </remarks>
    public static bool VerifyChain(IReadOnlyList<AuditEvent> ordered, string? expectedHead = null)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        var previous = GenesisHash;
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            if (e.Sequence != i + 1 || e.PreviousHash != previous || e.ComputeHash() != e.Hash)
            {
                return false;
            }

            previous = e.Hash;
        }

        return expectedHead is null || previous == expectedHead;
    }
}
