using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Domain.Cases;

namespace Desicon.Vim.Application.Cases;

/// <summary>
/// Runs every minute. Copies Microsoft Defender for Storage scan results onto
/// case documents. A malicious result raises a blocking flag; AP cannot act
/// until every document is Clean.
/// </summary>
public sealed class DocumentScanSync(ICaseRepository cases, IDocumentScanReader scanner, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var updated = 0;
        var ctx = ActionContext.For(Actor.System, clock.UtcNow);
        foreach (var c in await cases.GetWithPendingScansAsync(ct).ConfigureAwait(false))
        {
            foreach (var doc in c.Documents.Where(d => d.ScanStatus == ScanStatus.Pending).ToList())
            {
                var status = await scanner.GetScanStatusAsync(doc.BlobPath, ct).ConfigureAwait(false);
                if (status != ScanStatus.Pending)
                {
                    c.RecordScanResult(doc.Id, status, ctx);
                    updated++;
                }
            }
        }

        if (updated > 0)
        {
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return updated;
    }
}
