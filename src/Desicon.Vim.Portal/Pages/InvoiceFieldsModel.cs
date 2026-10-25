using Desicon.Vim.Domain.Cases;

namespace Desicon.Vim.Portal.Pages;

public sealed record InvoiceFieldsModel(InvoiceForm Input, SupplyType Supply, bool RequireAll, int MaxMegabytes);
