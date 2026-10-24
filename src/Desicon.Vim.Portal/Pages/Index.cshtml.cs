using Desicon.Vim.Application.VendorAccess;
using Desicon.Vim.Portal.Security;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Desicon.Vim.Portal.Pages;

public sealed class IndexModel(VendorAccessService access) : PageModel
{
    public PortalAccessResult Result { get; private set; } = new(PortalAccess.NotLinked, null, string.Empty);

    public async Task OnGetAsync(CancellationToken ct) =>
        Result = await access.ResolveAsync(PortalClaims.FromPrincipal(User), ct).ConfigureAwait(false);
}
