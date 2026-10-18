using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Web.Security;

namespace Desicon.Vim.Web.Endpoints;

/// <summary>
/// Streams a stored document to a signed-in user. Files are never exposed by
/// a public blob URL: every download passes through this check and is logged.
/// </summary>
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapVimDocuments(this IEndpointRouteBuilder app)
    {
        app.MapGet("/documents/{caseId:guid}/{documentId:guid}", async (
            Guid caseId,
            Guid documentId,
            HttpContext http,
            ICaseRepository cases,
            IDocumentStore store,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var actor = CurrentActor.FromPrincipal(http.User);
            var invoiceCase = await cases.GetAsync(caseId, ct).ConfigureAwait(false);
            var doc = invoiceCase?.Documents.SingleOrDefault(d => d.Id == documentId);
            if (invoiceCase is null || doc is null)
            {
                return Results.NotFound();
            }

            // Only files that passed the Defender malware scan are served.
            if (doc.ScanStatus != ScanStatus.Clean)
            {
                return Results.Problem(
                    doc.ScanStatus == ScanStatus.Pending
                        ? "This file is still being scanned for malware. Try again in a minute."
                        : "This file failed the malware scan and cannot be opened.",
                    statusCode: doc.ScanStatus == ScanStatus.Pending ? StatusCodes.Status409Conflict : StatusCodes.Status403Forbidden);
            }

            loggers.CreateLogger("Desicon.Vim.Documents").LogInformation(
                "Document {DocumentId} of {Reference} opened by {ActorId}.", doc.Id, invoiceCase.Reference, actor.Id);

            var stream = await store.OpenReadAsync(doc.BlobPath, ct).ConfigureAwait(false);
            return Results.File(stream, doc.ContentType, doc.FileName, enableRangeProcessing: true);
        }).RequireAuthorization(Policies.AnyVimUser);

        return app;
    }
}
