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
                files.Add(new SubmittedFile(kind, CleanFileName(f.FileName), f.ContentType, f.Length, _ => Task.FromResult(f.OpenReadStream())));
            }
        }

        return files;
    }

    public static string SlotName(DocumentKind kind) => "doc_" + kind;

    /// <summary>
    /// The browser decides the file name: keep only the last path segment (Windows or Unix
    /// separators), drop control characters and cap the length so it fits the database.
    /// </summary>
    public static string CleanFileName(string? raw)
    {
        var name = (raw ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string(name.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (name.Length == 0)
        {
            return "document.pdf";
        }

        const int Max = 150;
        if (name.Length > Max)
        {
            var ext = Path.GetExtension(name);
            ext = ext.Length is > 0 and <= 10 ? ext : string.Empty;
            name = name[..(Max - ext.Length)] + ext;
        }

        return name;
    }

    /// <summary>Whole request limit: every slot at the per-file maximum, plus form fields.</summary>
    public const long MaxRequestBytes = 9L * 26 * 1024 * 1024;
}
