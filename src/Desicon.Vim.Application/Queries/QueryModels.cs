using Desicon.Vim.Domain.Cases;

namespace Desicon.Vim.Application.Queries;

public sealed record CaseSearch(
    string? Text = null,
    IReadOnlyCollection<CaseStatus>? Statuses = null,
    string? CategoryCode = null,
    string? AssignedReviewerId = null,
    bool OverdueOnly = false,
    int Page = 1,
    int PageSize = 25);

public sealed record CaseSummary(
    Guid Id,
    string Reference,
    CaseStatus Status,
    SubmissionChannel Channel,
    DateTimeOffset ReceivedAtUtc,
    string? VendorName,
    string? SenderEmail,
    string? InvoiceNumber,
    decimal? Amount,
    string? Currency,
    string? OrderNumber,
    string? CategoryCode,
    string? AssignedReviewerName,
    DateTimeOffset? DueAtUtc,
    int OpenFlagCount,
    bool HasBlockingFlag);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public sealed record StageCount(CaseStatus Status, int Count);

public sealed record ReasonCount(string Reason, int Count);

public sealed record DashboardFigures(
    IReadOnlyList<StageCount> ByStatus,
    int OverdueCount,
    int ReceivedLast30Days,
    double? AverageHoursReceiptToVerified,
    double? AverageHoursVerifiedToErp,
    IReadOnlyList<ReasonCount> TopReturnReasons);
