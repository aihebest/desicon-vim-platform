using Desicon.Vim.Application.VendorPortal;
using Desicon.Vim.Domain.Cases;

namespace Desicon.Vim.Portal.Pages;

/// <summary>The invoice details a vendor types. All checks are the server-side submission rules.</summary>
public sealed class InvoiceForm
{
    public string InvoiceNumber { get; set; } = string.Empty;

    public DateOnly? InvoiceDate { get; set; }

    public decimal? Amount { get; set; }

    public string Currency { get; set; } = "NGN";

    public string OrderNumber { get; set; } = string.Empty;

    public DateOnly? DeliveredOn { get; set; }

    public VendorInvoiceForm ToForm(SupplyType supply) => new(
        InvoiceNumber,
        InvoiceDate ?? default,
        Amount ?? 0,
        Currency,
        OrderNumber,
        supply,
        DeliveredOn ?? default);

    public static InvoiceForm From(VendorInvoiceDetail d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new InvoiceForm
        {
            InvoiceNumber = d.Summary.InvoiceNumber ?? string.Empty,
            InvoiceDate = d.InvoiceDate,
            Amount = d.Summary.Amount,
            Currency = d.Summary.Currency ?? "NGN",
            OrderNumber = d.Summary.OrderNumber ?? string.Empty,
            DeliveredOn = d.DeliveredOn,
        };
    }
}
