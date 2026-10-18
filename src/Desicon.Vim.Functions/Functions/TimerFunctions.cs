using Desicon.Vim.Application.Cases;
using Desicon.Vim.Application.Intake;
using Desicon.Vim.Application.Sla;
using Desicon.Vim.Infrastructure.Messaging;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desicon.Vim.Functions;

// Schedules are NCRONTAB expressions (with seconds) read from app settings:
//   MailboxIntakeSchedule  default "0 */5 * * * *"   (every 5 minutes)
//   SlaMonitorSchedule     default "0 */15 * * * *"  (every 15 minutes)
//   OutboxDispatchSchedule default "0 * * * * *"     (every minute)
//   ScanResultSyncSchedule default "30 * * * * *"    (every minute, offset 30 s from the outbox)
// The %...% values are resolved by the Functions host, so each setting must
// exist in the function app's settings (and in local.settings.json locally).
// Timer functions run as a singleton across instances, so runs never overlap.

/// <summary>Reads the invoice mailbox and turns each new email into a case.</summary>
public sealed class MailboxIntakeFunction(
    GraphMailboxReader reader,
    IServiceScopeFactory scopeFactory,
    ILogger<MailboxIntakeFunction> logger)
{
    [Function("MailboxIntake")]
    public async Task RunAsync(
        [TimerTrigger("%MailboxIntakeSchedule%", RunOnStartup = false)] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);
        var result = await reader.ProcessUnreadAsync(ProcessOneAsync, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Mailbox intake: {Processed} processed, {Failed} failed, {Skipped} skipped. Next run {NextRun}.",
            result.Processed,
            result.Failed,
            result.Skipped,
            timer.ScheduleStatus?.Next);
    }

    /// <summary>Each email gets its own DI scope, so one email's database context never leaks into the next.</summary>
    private async Task ProcessOneAsync(InboundEmail email, CancellationToken ct)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var intake = scope.ServiceProvider.GetRequiredService<IntakeService>();
            var outcome = await intake.ProcessEmailAsync(email, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Email {MessageId}: {Outcome} {Reference}.",
                email.MessageId,
                outcome.Outcome,
                outcome.CaseReference);
        }
    }
}

/// <summary>Sends reminders half-way to the review deadline and escalates overdue cases.</summary>
public sealed class SlaMonitorFunction(SlaMonitor monitor, ILogger<SlaMonitorFunction> logger)
{
    [Function("SlaMonitor")]
    public async Task RunAsync(
        [TimerTrigger("%SlaMonitorSchedule%", RunOnStartup = false)] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);
        var result = await monitor.RunAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "SLA monitor: {Reminders} reminder(s), {Escalations} escalation(s).",
            result.RemindersSent,
            result.Escalations);
    }
}

/// <summary>Sends queued notification emails through Microsoft Graph.</summary>
public sealed class OutboxDispatchFunction(OutboxDispatcher dispatcher, ILogger<OutboxDispatchFunction> logger)
{
    [Function("OutboxDispatch")]
    public async Task RunAsync(
        [TimerTrigger("%OutboxDispatchSchedule%", RunOnStartup = false)] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);
        var result = await dispatcher.DispatchAsync(cancellationToken).ConfigureAwait(false);
        if (result.Sent + result.Failed > 0)
        {
            logger.LogInformation("Outbox: {Sent} sent, {Failed} failed.", result.Sent, result.Failed);
        }
    }
}

/// <summary>
/// Copies Microsoft Defender for Storage malware-scan verdicts (blob index tags) onto case
/// documents. A malicious file raises a blocking flag on its case.
/// </summary>
public sealed class ScanResultSyncFunction(DocumentScanSync scanSync, ILogger<ScanResultSyncFunction> logger)
{
    [Function("ScanResultSync")]
    public async Task RunAsync(
        [TimerTrigger("%ScanResultSyncSchedule%", RunOnStartup = false)] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);
        var updated = await scanSync.RunAsync(cancellationToken).ConfigureAwait(false);
        if (updated > 0)
        {
            logger.LogInformation("Scan result sync: {Updated} document scan result(s) recorded.", updated);
        }
    }
}
