using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Application.Submission;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Checklists;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Application.Cases;

/// <summary>Thrown when a case or route does not exist.</summary>
public sealed class NotFoundException(string message) : Exception(message)
{
    public NotFoundException()
        : this("Not found.")
    {
    }

    public NotFoundException(string message, Exception inner)
        : this(message + " " + inner.Message)
    {
    }
}

/// <summary>
/// The people-driven steps of workflow v2: the Finance Manager decides exceptions,
/// Accounts does the confirmatory check (acceptance starts the vendor's credit period)
/// and records the Business Central entry. Each method loads the case, applies one
/// domain action, queues notifications and saves everything in one transaction.
/// </summary>
public sealed class CaseWorkflowService(
    ICaseRepository cases,
    INotificationOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<VimOptions> options)
{
    private readonly ChecklistDefinition _checklist = DesiconInvoiceChecklist.Current;

    public ChecklistDefinition Checklist => _checklist;

    /// <summary>Accounts: confirmatory check passed. The vendor is told; the credit period starts now.</summary>
    public async Task AcceptAsync(Guid caseId, IReadOnlyCollection<ChecklistResponse> answers, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        c.Accept(answers, _checklist, Ctx(actor));
        if (!string.IsNullOrWhiteSpace(c.SenderEmail))
        {
            outbox.Enqueue(NotificationTemplates.Accepted(c, c.SenderEmail));
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Accounts (confirmatory check) or the Finance Manager (exception): reject with reasons.</summary>
    public async Task ReturnToVendorAsync(Guid caseId, string reason, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        c.ReturnToVendor(reason, Ctx(actor));
        if (!string.IsNullOrWhiteSpace(c.SenderEmail))
        {
            outbox.Enqueue(NotificationTemplates.ReturnedToVendor(c, c.SenderEmail, reason));
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Finance Manager: flags cleared; Accounts gets the case with a fresh deadline.</summary>
    public async Task ReleaseToAccountsAsync(Guid caseId, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        var ctx = Ctx(actor);
        var o = options.Value;
        c.ReleaseToAccounts(o.Deadlines.Create(SubmissionProcessor.AccountsHolder, ctx.NowUtc, o.AccountsCheckWorkingDays), ctx);
        if (!string.IsNullOrWhiteSpace(o.AccountsPayableEmail))
        {
            outbox.Enqueue(NotificationTemplates.ReadyForAccounts(c, o.AccountsPayableEmail, o.WorkbenchUrl));
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordErpEntryAsync(Guid caseId, string erpDocumentNumber, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        c.RecordErpEntry(erpDocumentNumber, Ctx(actor));
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task CancelAsync(Guid caseId, string reason, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        c.Cancel(reason, Ctx(actor));
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ResolveFlagAsync(Guid caseId, Guid flagId, string resolution, Actor actor, CancellationToken ct)
    {
        var c = await LoadAsync(caseId, ct).ConfigureAwait(false);
        c.ResolveFlag(flagId, resolution, Ctx(actor));
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<InvoiceCase> LoadAsync(Guid id, CancellationToken ct) =>
        await cases.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Case {id} was not found.");

    private ActionContext Ctx(Actor actor) => ActionContext.For(actor, clock.UtcNow);
}
