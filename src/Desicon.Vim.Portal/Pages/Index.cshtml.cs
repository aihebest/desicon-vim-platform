using Desicon.Vim.Application.VendorAccess;
using Desicon.Vim.Application.VendorPortal;
using Desicon.Vim.Portal.Security;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Desicon.Vim.Portal.Pages;

public sealed class IndexModel(VendorAccessService access, VendorPortalService portal) : PageModel
{
    public PortalAccessResult Result { get; private set; } = new(PortalAccess.NotLinked, null, string.Empty);

    public IReadOnlyList<VendorInvoiceSummary> Invoices { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Result = await access.ResolveAsync(PortalClaims.FromPrincipal(User), ct).ConfigureAwait(false);
        if (Result is { Access: PortalAccess.Allowed, User: { } user })
        {
            Invoices = await portal.ListAsync(user, ct).ConfigureAwait(false);
        }
    }
}
