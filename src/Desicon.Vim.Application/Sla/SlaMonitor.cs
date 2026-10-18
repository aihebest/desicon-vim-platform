using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Application.Notifications;
using Desicon.Vim.Domain.Cases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Application.Sla;

public sealed record SlaRunResult(int RemindersSent, int Escalations);

/// <summary>
/// Runs on a timer (every 15 minutes). Sends a reminder half-way to the
/// deadline and escalates to the Finance Manager once it has passed.
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
            if (c.AssignedReviewerId is null)
            {
                continue;
            }

            outbox.Enqueue(NotificationTemplates.Reminder(c, c.AssignedReviewerId, o.WorkbenchUrl));
            c.MarkReminderSent(ctx);
            reminders++;
        }

        var escalations = 0;
        if (!string.IsNullOrWhiteSpace(o.FinanceManagerEmail))
        {
            foreach (var c in await cases.GetOverdueNotEscalatedAsync(now, ct).ConfigureAwait(false))
            {
                outbox.Enqueue(NotificationTemplates.Escalation(c, o.FinanceManagerEmail, o.WorkbenchUrl));
                c.MarkEscalated(o.FinanceManagerEmail, ctx);
                escalations++;
            }
        }
        else
        {
            logger.LogWarning("Vim:FinanceManagerEmail is not configured; overdue cases are not being escalated.");
        }

        if (reminders + escalations > 0)
        {
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new SlaRunResult(reminders, escalations);
    }
}
