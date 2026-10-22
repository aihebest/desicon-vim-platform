using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Domain.Cases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Application.Sla;

public sealed record SlaRunResult(int RemindersSent, int Escalations);

/// <summary>
/// Runs on a timer (every 15 minutes). Sends the holder (Accounts or the Finance Manager)
/// a reminder half-way to the deadline and escalates once it has passed, so the
/// 3-working-day target is visible and enforced.
/// </summary>
public sealed class SlaMonitor(
    ICaseRepository cases,
    INotificationOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<VimOptions> options,
    ILogger<SlaMonitor> logger)
{
    public async Task<SlaRunResult> RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var ctx = ActionContext.For(Actor.System, now);
        var o = options.Value;

        var reminders = 0;
        foreach (var c in await cases.GetDueForReminderAsync(now, ct).ConfigureAwait(false))
        {
            var holderEmail = c.Status == CaseStatus.ExceptionReview ? o.FinanceManagerEmail : o.AccountsPayableEmail;
            if (string.IsNullOrWhiteSpace(holderEmail))
            {
                continue;
            }

            outbox.Enqueue(NotificationTemplates.Reminder(c, holderEmail, o.WorkbenchUrl));
            c.MarkReminderSent(ctx);
            reminders++;
        }

        // Accounts overdue -> Finance Manager. Finance Manager overdue -> management (or the FM again if not set).
        var escalations = 0;
        foreach (var c in await cases.GetOverdueNotEscalatedAsync(now, ct).ConfigureAwait(false))
        {
            var to = c.Status == CaseStatus.ExceptionReview && !string.IsNullOrWhiteSpace(o.ManagementEscalationEmail)
                ? o.ManagementEscalationEmail
                : o.FinanceManagerEmail;
            if (string.IsNullOrWhiteSpace(to))
            {
                logger.LogWarning("Vim:FinanceManagerEmail is not configured; overdue case {Reference} is not being escalated.", c.Reference);
                continue;
            }

            outbox.Enqueue(NotificationTemplates.Escalation(c, to, o.WorkbenchUrl));
            c.MarkEscalated(to, ctx);
            escalations++;
        }

        if (reminders + escalations > 0)
        {
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new SlaRunResult(reminders, escalations);
    }
}
