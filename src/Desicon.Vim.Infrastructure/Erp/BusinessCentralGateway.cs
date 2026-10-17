using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Desicon.Vim.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Infrastructure.Erp;

/// <summary>
/// Read-only Business Central adapter over the standard API v2.0
/// (https://api.businesscentral.dynamics.com/v2.0/{tenant}/{environment}/api/v2.0/companies({id})/).
/// The platform never creates, posts or pays anything in Business Central.
/// Assumptions are listed in docs/operations/business-central.md:
/// <list type="bullet">
/// <item>vendors: <c>number</c>, <c>displayName</c>, <c>email</c>, <c>blocked</c> (" ", "Payment", "All").</item>
/// <item>purchaseOrders (+ $expand=purchaseOrderLines): an order exists only until it is fully invoiced;
/// API status "Open" = Released in the BC client; a posted receipt is detected from line <c>receivedQuantity</c>.</item>
/// <item>purchaseInvoices: posted invoices carry status "Open" or "Paid"; the API exposes no payment date or
/// payment document number, so <see cref="ErpPaymentStatus.PaidOn"/> is approximated by <c>lastModifiedDateTime</c>.</item>
/// <item>Vendor bank accounts are not exposed by API v2.0 — an AL custom API page is needed (Phase 2).</item>
/// </list>
/// Email filters in BC OData are case-sensitive; the sender address is compared as received (lower-cased by the case).
/// </summary>
public sealed class BusinessCentralGateway(
    HttpClient http,
    IOptions<BusinessCentralOptions> options,
    ILogger<BusinessCentralGateway> logger) : IErpGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsEnabled => options.Value.IsEnabled;

    public async Task<ErpVendor?> GetVendorAsync(string vendorNo, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vendorNo);
        var vendors = await GetListAsync<BcVendor>("vendors?$filter=" + Filter($"number eq {Literal(vendorNo.Trim())}"), ct).ConfigureAwait(false);
        return vendors.Count == 0 ? null : ToErpVendor(vendors[0]);
    }

    public async Task<IReadOnlyList<ErpVendor>> FindVendorsByEmailAsync(string email, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var vendors = await GetListAsync<BcVendor>("vendors?$filter=" + Filter($"email eq {Literal(email.Trim())}"), ct).ConfigureAwait(false);
        return vendors.Select(ToErpVendor).ToList();
    }

    public async Task<ErpOrderCheck> CheckOrderAsync(string vendorNo, string orderNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNumber);
        var orders = await GetListAsync<BcPurchaseOrder>(
            "purchaseOrders?$filter=" + Filter($"number eq {Literal(orderNumber.Trim())}") + "&$expand=purchaseOrderLines",
            ct).ConfigureAwait(false);

        if (orders.Count == 0)
        {
            return new ErpOrderCheck(OrderFound: false, BelongsToVendor: false, IsOpen: false, HasPostedReceipt: false, RemainingAmount: null, Currency: null);
        }

        var order = orders[0];
        var lines = order.PurchaseOrderLines ?? [];
        var hasReceipt = order.FullyReceived || lines.Any(l => l.ReceivedQuantity > 0);

        // Remaining (not yet invoiced) value including tax, pro-rata per line.
        decimal? remaining = null;
        if (order.PurchaseOrderLines is not null)
        {
            remaining = decimal.Round(
                lines.Where(l => l.Quantity > 0)
                    .Sum(l => l.AmountIncludingTax * (l.Quantity - l.InvoicedQuantity) / l.Quantity),
                2);
        }

        var currency = string.IsNullOrWhiteSpace(order.CurrencyCode) ? options.Value.LocalCurrencyCode : order.CurrencyCode.Trim();

        return new ErpOrderCheck(
            OrderFound: true,
            BelongsToVendor: string.Equals(order.VendorNumber, vendorNo?.Trim(), StringComparison.OrdinalIgnoreCase),
            IsOpen: string.Equals(order.Status, "Open", StringComparison.OrdinalIgnoreCase),
            HasPostedReceipt: hasReceipt,
            RemainingAmount: remaining,
            Currency: currency);
    }

    public async Task<ErpPaymentStatus?> GetPaymentStatusAsync(string erpDocumentNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(erpDocumentNumber);
        var invoices = await GetListAsync<BcPurchaseInvoice>(
            "purchaseInvoices?$filter=" + Filter($"number eq {Literal(erpDocumentNumber.Trim())}"),
            ct).ConfigureAwait(false);

        if (invoices.Count == 0)
        {
            return null;
        }

        var invoice = invoices[0];
        if (!string.Equals(invoice.Status, "Paid", StringComparison.OrdinalIgnoreCase))
        {
            return new ErpPaymentStatus(Paid: false, PaymentReference: null, PaidOn: null);
        }

        // Approximation: BC does not expose the payment date on purchaseInvoices (see class remarks).
        DateOnly? paidOn = invoice.LastModifiedDateTime is { } modified ? DateOnly.FromDateTime(modified.UtcDateTime) : null;
        return new ErpPaymentStatus(Paid: true, PaymentReference: invoice.Number, PaidOn: paidOn);
    }

    /// <summary>OData string literal: wrapped in single quotes, embedded quotes doubled (O'Neil → 'O''Neil').</summary>
    public static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string Filter(string expression) => Uri.EscapeDataString(expression);

    private static ErpVendor ToErpVendor(BcVendor v) => new(
        v.Number ?? string.Empty,
        v.DisplayName ?? string.Empty,
        Blocked: !string.IsNullOrWhiteSpace(v.Blocked),
        Emails: string.IsNullOrWhiteSpace(v.Email) ? [] : [v.Email.Trim()],
        // Not available from the standard API v2.0; requires a custom API page (Phase 2).
        BankAccountNumbers: []);

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string relativeUrl, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(relativeUrl, UriKind.Relative), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            logger.LogError(
                "Business Central request {Url} failed with {StatusCode}: {Body}",
                relativeUrl,
                (int)response.StatusCode,
                body.Length > 1000 ? body[..1000] : body);
            response.EnsureSuccessStatusCode();
        }

        var page = await response.Content.ReadFromJsonAsync<ODataList<T>>(Json, ct).ConfigureAwait(false);
        return page?.Value ?? [];
    }

    private sealed class ODataList<T>
    {
        public List<T>? Value { get; set; }
    }

    private sealed class BcVendor
    {
        public string? Number { get; set; }

        public string? DisplayName { get; set; }

        public string? Email { get; set; }

        /// <summary>" " (not blocked), "Payment" or "All".</summary>
        public string? Blocked { get; set; }
    }

    private sealed class BcPurchaseOrder
    {
        public string? Number { get; set; }

        public string? VendorNumber { get; set; }

        /// <summary>"Draft", "In Review" or "Open" (Released).</summary>
        public string? Status { get; set; }

        public string? CurrencyCode { get; set; }

        public decimal TotalAmountIncludingTax { get; set; }

        public bool FullyReceived { get; set; }

        public List<BcPurchaseOrderLine>? PurchaseOrderLines { get; set; }
    }

    private sealed class BcPurchaseOrderLine
    {
        public decimal Quantity { get; set; }

        public decimal ReceivedQuantity { get; set; }

        public decimal InvoicedQuantity { get; set; }

        public decimal AmountIncludingTax { get; set; }
    }

    private sealed class BcPurchaseInvoice
    {
        public string? Number { get; set; }

        /// <summary>"Draft", "In Review", "Open", "Paid", "Canceled" or "Corrective".</summary>
        public string? Status { get; set; }

        public DateTimeOffset? LastModifiedDateTime { get; set; }
    }
}

/// <summary>Registered when Business Central is switched off or not configured. Validation is skipped.</summary>
public sealed class DisabledErpGateway : IErpGateway
{
    public bool IsEnabled => false;

    public Task<ErpVendor?> GetVendorAsync(string vendorNo, CancellationToken ct) => Task.FromResult<ErpVendor?>(null);

    public Task<IReadOnlyList<ErpVendor>> FindVendorsByEmailAsync(string email, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ErpVendor>>([]);

    public Task<ErpOrderCheck> CheckOrderAsync(string vendorNo, string orderNumber, CancellationToken ct) =>
        Task.FromResult(new ErpOrderCheck(false, false, false, false, null, null));

    public Task<ErpPaymentStatus?> GetPaymentStatusAsync(string erpDocumentNumber, CancellationToken ct) =>
        Task.FromResult<ErpPaymentStatus?>(null);
}

/// <summary>Holds the app-only credential for Business Central. Singleton, so MSAL's token cache is reused.</summary>
public sealed class BusinessCentralTokenSource
{
    private static readonly string[] Scopes = ["https://api.businesscentral.dynamics.com/.default"];

    private readonly ClientSecretCredential _credential;

    public BusinessCentralTokenSource(IOptions<BusinessCentralOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var o = options.Value;
        _credential = new ClientSecretCredential(
            o.TenantId ?? throw new InvalidOperationException("BusinessCentral:TenantId is not configured."),
            o.ClientId ?? throw new InvalidOperationException("BusinessCentral:ClientId is not configured."),
            o.ClientSecret ?? throw new InvalidOperationException("BusinessCentral:ClientSecret is not configured."));
    }

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct).ConfigureAwait(false);
        return token.Token;
    }
}

/// <summary>Adds the bearer token to every Business Central request.</summary>
public sealed class BusinessCentralAuthHandler(BusinessCentralTokenSource tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
