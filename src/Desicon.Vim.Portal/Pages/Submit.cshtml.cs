using Desicon.Vim.Application;
using Desicon.Vim.Application.VendorAccess;
using Desicon.Vim.Application.VendorPortal;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Portal.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Portal.Pages;

[RequestSizeLimit(VendorPageModel.MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = VendorPageModel.MaxRequestBytes)]
public sealed class SubmitModel(VendorAccessService access, VendorPortalService portal, IOptions<VimOptions> options) : VendorPageModel(access)
{
    [BindProperty(SupportsGet = true)]
    public SupplyType Supply { get; set; }

    [BindProperty]
    public InvoiceForm Input { get; set; } = new();

    public IReadOnlyList<string> Problems { get; private set; } = [];

    public InvoiceFieldsModel Fields => new(Input, Supply, RequireAll: true, options.Value.MaxAttachmentMegabytes);

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Supply is not (SupplyType.Materials or SupplyType.Services))
        {
            return RedirectToPage();
        }

        var slots = RequiredDocuments.For(Supply).Concat(RequiredDocuments.Optional(Supply));
        var result = await portal.SubmitAsync(Vendor, Input.ToForm(Supply), FilesFromForm(slots), ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            Problems = result.Problems;
            return Page();
        }

        TempData["Notice"] = $"Invoice received. Your reference is {result.Reference}. We will email you as it progresses.";
        return Redirect($"/invoices/{result.CaseId}");
    }
}
