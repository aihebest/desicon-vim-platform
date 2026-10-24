using Desicon.Vim.Application.VendorAccess;
using Desicon.Vim.Portal.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Desicon.Vim.Portal.Pages;

/// <summary>
/// Opening the link signs the person in (External ID) and shows the invitation; only the
/// explicit button press links the login, so a mail scanner opening the link changes nothing.
/// </summary>
public sealed class InviteModel(VendorAccessService access) : PageModel
{
    public InvitationPreview? Preview { get; private set; }

    public PortalAccessResult? Outcome { get; private set; }

    public string SignedInEmail { get; private set; } = string.Empty;

    public async Task OnGetAsync(string token, CancellationToken ct)
    {
        SignedInEmail = PortalClaims.FromPrincipal(User).Email;
        Preview = await access.PreviewInvitationAsync(token, ct).ConfigureAwait(false);
    }

    public async Task<IActionResult> OnPostAsync(string token, CancellationToken ct)
    {
        var signIn = PortalClaims.FromPrincipal(User);
        SignedInEmail = signIn.Email;
        Outcome = await access.AcceptInvitationAsync(token, signIn, ct).ConfigureAwait(false);
        return Page();
    }
}
