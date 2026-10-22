using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Queries;
using Desicon.Vim.Domain.Audit;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Vim.Infrastructure.Persistence;

/// <summary>
/// Read-only queries for the workbench lists and dashboard. Everything is
/// AsNoTracking and projected in SQL; nothing here changes data.
/// </summary>
public sealed class EfCaseQueries(VimDbContext db, IClock clock) : ICaseQueries
{
    public const int MaxPageSize = 200;

    /// <summary>Dashboard averages and return reasons look at this many days of history.</summary>
    public const int DashboardWindowDays = 90;

    private const string AcceptAction = nameof(CaseAction.Accept);
    private const string RecordErpEntryAction = nameof(CaseAction.RecordErpEntry);
    private const string ReturnToVendorAction = nameof(CaseAction.ReturnToVendor);

    public async Task<PagedResult<CaseSummary>> SearchAsync(CaseSearch search, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(search);
        var page = Math.Max(1, search.Page);
        var pageSize = Math.Clamp(search.PageSize, 1, MaxPageSize);

        var query = db.Cases.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search.Text))
        {
            var text = search.Text.Trim();
            query = query.Where(c =>
                c.Reference.Contains(text)
                || (c.VendorName != null && c.VendorName.Contains(text))
                || (c.InvoiceNumber != null && c.InvoiceNumber.Contains(text))
                || (c.OrderNumber != null && c.OrderNumber.Contains(text))
                || (c.SenderEmail != null && c.SenderEmail.Contains(text)));
        }

        if (search.Statuses is { Count: > 0 })
        {
            var statuses = search.Statuses.ToList();
            query = query.Where(c => statuses.Contains(c.Status));
        }

        if (!string.IsNullOrWhiteSpace(search.CategoryCode))
        {
            var category = search.CategoryCode.Trim().ToUpperInvariant();
            query = query.Where(c => c.CategoryCode == category);
        }

        if (!string.IsNullOrWhiteSpace(search.AssignedReviewerId))
        {
            var reviewer = search.AssignedReviewerId;
            query = query.Where(c => c.AssignedReviewerId == reviewer);
        }

        if (search.OverdueOnly)
        {
            var now = clock.UtcNow;
            query = query.Where(c => c.DueAtUtc != null
                && c.DueAtUtc < now
                && c.Status != CaseStatus.Closed
                && c.Status != CaseStatus.Cancelled);
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(c => c.ReceivedAtUtc)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CaseSummary(
                c.Id,
                c.Reference,
                c.Status,
                c.Channel,
                c.ReceivedAtUtc,
                c.VendorName,
                c.SenderEmail,
                c.InvoiceNumber,
                c.Amount,
                c.Currency,
                c.OrderNumber,
                c.CategoryCode,
                c.AssignedReviewerName,
                c.DueAtUtc,
                c.Flags.Count(f => f.ResolvedAtUtc == null),
                c.Flags.Any(f => f.ResolvedAtUtc == null && f.Blocking)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<CaseSummary>(items, total, page, pageSize);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetAuditTrailAsync(Guid caseId, CancellationToken ct) =>
        await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.CaseId == caseId)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<DashboardFigures> GetDashboardAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var cases = db.Cases.AsNoTracking();
        var events = db.AuditEvents.AsNoTracking();
        var since = nowUtc.AddDays(-DashboardWindowDays);
        var last30 = nowUtc.AddDays(-30);

        var byStatusRows = await cases
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byStatus = byStatusRows
            .OrderBy(r => r.Status)
            .Select(r => new StageCount(r.Status, r.Count))
            .ToList();

        var overdue = await cases
            .CountAsync(c => c.DueAtUtc != null
                && c.DueAtUtc < nowUtc
                && c.Status != CaseStatus.Closed
                && c.Status != CaseStatus.Cancelled, ct)
            .ConfigureAwait(false);

        var receivedLast30 = await cases.CountAsync(c => c.ReceivedAtUtc >= last30, ct).ConfigureAwait(false);

        // Submission → acceptance (official submission): each Accept event against the case's receipt time.
        var minutesToAccepted = await (
                from e in events
                where e.Action == AcceptAction && e.OccurredAtUtc >= since
                join c in cases on e.CaseId equals c.Id
                select (double?)EF.Functions.DateDiffMinute(c.ReceivedAtUtc, e.OccurredAtUtc))
            .AverageAsync(ct)
            .ConfigureAwait(false);

        // Acceptance → ERP entry: each RecordErpEntry event against the latest Accept before it.
        var minutesAcceptedToErp = await (
                from e in events
                where e.Action == RecordErpEntryAction && e.OccurredAtUtc >= since
                let verifiedAt = events
                    .Where(v => v.CaseId == e.CaseId && v.Action == AcceptAction && v.Sequence < e.Sequence)
                    .Max(v => (DateTimeOffset?)v.OccurredAtUtc)
                where verifiedAt != null
                select (double?)EF.Functions.DateDiffMinute(verifiedAt, e.OccurredAtUtc))
            .AverageAsync(ct)
            .ConfigureAwait(false);

        var reasonRows = await events
            .Where(e => e.Action == ReturnToVendorAction && e.Reason != null && e.OccurredAtUtc >= since)
            .GroupBy(e => e.Reason!)
            .Select(g => new { Reason = g.Key, Count = g.Count() })
            .OrderByDescending(r => r.Count)
            .Take(5)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new DashboardFigures(
            byStatus,
            overdue,
            receivedLast30,
            minutesToAccepted / 60d,
            minutesAcceptedToErp / 60d,
            reasonRows.Select(r => new ReasonCount(r.Reason, r.Count)).ToList());
    }
}
