using Desicon.Vim.Application.Submission;
using Desicon.Vim.Application.VendorAccess;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Domain.VendorAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Desicon.Vim.Portal.Security;

/// <summary>
/// Base for pages a linked vendor uses. Access is re-checked on every request (removed,
/// dormant or BC-blocked users are sent back to the home page, which explains why), and
/// the vendor always comes from the login, never from the browser.
/// </summary>
public abstract class VendorPageModel(VendorAccessService access) : PageModel
{
    protected VendorUser Vendor { get; private set; } = default!;

    public string VendorName => Vendor.VendorName;

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var result = await access.ResolveAsync(PortalClaims.FromPrincipal(User), HttpContext.RequestAborted).ConfigureAwait(false);
        if (result.Access != PortalAccess.Allowed || result.User is null)
        {
            context.Result = RedirectToPage("/Index");
            return;
        }

        Vendor = result.User;
        await next().ConfigureAwait(false);
    }

    /// <summary>The PDF uploaded in each document slot (input name "doc_{Kind}").</summary>
    protected IReadOnlyList<SubmittedFile> FilesFromForm(IEnumerable<DocumentKind> slots)
    {
        var files = new List<SubmittedFile>();
        foreach (var kind in slots.Distinct())
        {
            var f = Request.Form.Files.GetFile(SlotName(kind));
            if (f is { Length: > 0 })
            {
                files.Add(new SubmittedFile(kind, Path.GetFileName(f.FileName), f.ContentType, f.Length, _ => Task.FromResult(f.OpenReadStream())));
            }
        }

        return files;
    }

    public static string SlotName(DocumentKind kind) => "doc_" + kind;

    /// <summary>Whole request limit: every slot at the per-file maximum, plus form fields.</summary>
    public const long MaxRequestBytes = 9L * 26 * 1024 * 1024;
}
