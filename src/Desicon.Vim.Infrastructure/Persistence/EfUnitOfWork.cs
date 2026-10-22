using Desicon.Vim.Application;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.Integrity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Desicon.Vim.Infrastructure.Persistence;

/// <summary>
/// Saves one operation in a single transaction: case changes, the audit
/// events the aggregate produced, outbox messages and the inbound-message log.
/// </summary>
public sealed class EfUnitOfWork(VimDbContext db) : IUnitOfWork
{
    public void DiscardChanges() => db.ChangeTracker.Clear();

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        // Entries() runs DetectChanges, so new child rows (documents, flags...) are picked up first.
        var caseEntries = db.ChangeTracker.Entries<InvoiceCase>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Unchanged)
            .ToList();

        foreach (var entry in caseEntries)
        {
            var invoiceCase = entry.Entity;

            SetIfChanged(entry, VimDbContext.VendorNameKeyColumn, DuplicateDetector.NormaliseKey(invoiceCase.VendorName));
            SetIfChanged(entry, VimDbContext.InvoiceNumberKeyColumn, DuplicateDetector.NormaliseInvoiceNumber(invoiceCase.InvoiceNumber));

            // The aggregate keeps unsaved audit events in memory; they go to the insert-only table here.
            // Adding an already-tracked event again (e.g. retry after a failed save) is a no-op.
            foreach (var auditEvent in invoiceCase.NewAuditEvents)
            {
                db.AuditEvents.Add(auditEvent);
            }
        }

        // Only concurrency conflicts are translated. Other DbUpdateExceptions surface unchanged, e.g. a
        // primary-key violation on InboundMessages when two runs process the same email at once:
        // the database rejects the duplicate and the caller logs it and moves on.
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException(ConcurrencyConflictException.DefaultMessage, ex);
        }

        foreach (var entry in caseEntries)
        {
            entry.Entity.ClearNewAuditEvents();
        }
    }

    private static void SetIfChanged(EntityEntry<InvoiceCase> entry, string propertyName, string value)
    {
        var property = entry.Property<string>(propertyName);
        if (!string.Equals(property.CurrentValue, value, StringComparison.Ordinal))
        {
            property.CurrentValue = value;
        }
    }
}
