using Desicon.Vim.Application;
using Desicon.Vim.Application.Cases;
using Desicon.Vim.Application.VendorAccess;
using Desicon.Vim.Application.VendorPortal;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Portal.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Portal.Pages;

[RequestSizeLimit(VendorPageModel.MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = VendorPageModel.MaxRequestBytes)]
public sealed class InvoiceModel(VendorAccessService access, VendorPortalService portal, IOptions<VimOptions> options) : VendorPageModel(access)
{
    public VendorInvoiceDetail Detail { get; private set; } = default!;

    [BindProperty]
    public InvoiceForm Input { get; set; } = new();

    public IReadOnlyList<string> Problems { get; private set; } = [];

    public InvoiceFieldsModel Fields => new(Input, Detail.SupplyType, RequireAll: false, options.Value.MaxAttachmentMegabytes);

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var detail = await portal.GetAsync(Vendor, id, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return NotFound();
        }

        Detail = detail;
        Input = InvoiceForm.From(detail);
        return Page();
    }

    public async Task<IActionResult> OnPostResubmitAsync(Guid id, CancellationToken ct)
    {
        var detail = await portal.GetAsync(Vendor, id, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return NotFound();
        }

        Detail = detail;
        var slots = RequiredDocuments.For(detail.SupplyType).Concat(RequiredDocuments.Optional(detail.SupplyType));
        VendorSubmitResult result;
        try
        {
            result = await portal.ResubmitAsync(Vendor, id, Input.ToForm(detail.SupplyType), FilesFromForm(slots), ct).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            Problems = result.Problems;
            return Page();
        }

        TempData["Notice"] = "Correction received. The automatic checks run again; we will email you as it progresses.";
        return Redirect($"/invoices/{id}");
    }
}
