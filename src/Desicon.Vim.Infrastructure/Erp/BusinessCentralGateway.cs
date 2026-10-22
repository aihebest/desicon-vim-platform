using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Desicon.Vim.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Infrastructure.Erp;

/// <summary>
/// Read-only Business Central adapter over the standard API v2.0
/// (https://api.businesscentral.dynamics.com/v2.0/{tenant}/{environment}/api/v2.0/companies({id})/).
/// The company id is configured, or looked up once by name (<see cref="BusinessCentralCompany"/>).
/// The platform never creates, posts or pays anything in Business Central.
/// Assumptions are listed in docs/operations/business-central.md:
/// <list type="bullet">
/// <item>vendors: <c>number</c>, <c>displayName</c>, <c>email</c>, <c>blocked</c> ("_x0020_" = not blocked, "Payment", "All").</item>
/// <item>purchaseOrders (+ $expand=purchaseOrderLines): an order exists only until it is fully invoiced;
/// API status "Open" = Released in the BC client; received-not-invoiced value = line amount incl. tax ×
/// (<c>receivedQuantity</c> − <c>invoicedQuantity</c>) / <c>quantity</c>.</item>
/// <item>purchaseInvoices: posted invoices carry status "Open" or "Paid"; the API exposes no payment date or
/// payment document number, so <see cref="ErpPaymentStatus.PaidOn"/> is approximated by <c>lastModifiedDateTime</c>.</item>
/// <item>Vendor bank accounts are not exposed by API v2.0 — an AL custom API page is needed (Phase 2).</item>
/// </list>
/// Email filters in BC OData are case-sensitive; the sender address is compared as received (lower-cased by the case).
/// </summary>
public sealed class BusinessCentralGateway(
    HttpClient http,
    IOptions<BusinessCentralOptions> options,
    BusinessCentralCompany company,
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

        // Received but not yet invoiced, including tax, pro-rata per line: what a full-value invoice must equal.
        decimal? receivedNotInvoiced = null;
        decimal? remaining = null;
        if (order.PurchaseOrderLines is not null)
        {
            var priced = lines.Where(l => l.Quantity > 0).ToList();
            receivedNotInvoiced = decimal.Round(
                priced.Sum(l => l.AmountIncludingTax * Math.Max(0, l.ReceivedQuantity - l.InvoicedQuantity) / l.Quantity),
                2);
            remaining = decimal.Round(
                priced.Sum(l => l.AmountIncludingTax * (l.Quantity - l.InvoicedQuantity) / l.Quantity),
                2);
        }

        var hasReceipt = receivedNotInvoiced > 0 || (order.PurchaseOrderLines is null && order.FullyReceived);
        var currency = string.IsNullOrWhiteSpace(order.CurrencyCode) ? options.Value.LocalCurrencyCode : order.CurrencyCode.Trim();

        return new ErpOrderCheck(
            OrderFound: true,
            BelongsToVendor: string.Equals(order.VendorNumber, vendorNo?.Trim(), StringComparison.OrdinalIgnoreCase),
            IsOpen: string.Equals(order.Status, "Open", StringComparison.OrdinalIgnoreCase),
            HasPostedReceipt: hasReceipt,
            RemainingAmount: remaining,
            Currency: currency,
            ReceivedNotInvoicedAmount: receivedNotInvoiced);
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

    /// <summary>
    /// BC's vendor "blocked" option is " " (not blocked), "Payment" or "All". The API
    /// serialises the blank option as "_x0020_" (an encoded space), so only the two
    /// real values count as blocked.
    /// </summary>
    internal static bool IsBlocked(string? blocked) =>
        string.Equals(blocked?.Trim(), "Payment", StringComparison.OrdinalIgnoreCase)
        || string.Equals(blocked?.Trim(), "All", StringComparison.OrdinalIgnoreCase);

    private static ErpVendor ToErpVendor(BcVendor v) => new(
        v.Number ?? string.Empty,
        v.DisplayName ?? string.Empty,
        Blocked: IsBlocked(v.Blocked),
        Emails: string.IsNullOrWhiteSpace(v.Email) ? [] : [v.Email.Trim()],
        // Not available from the standard API v2.0; requires a custom API page (Phase 2).
        BankAccountNumbers: []);

    /// <summary>
    /// Reads the company and one vendor, to prove sign-in and the D365 READ permission.
    /// Returns a one-line description for the admin health page.
    /// </summary>
    internal async Task<string> TestConnectionAsync(CancellationToken ct)
    {
        var companyId = await company.GetIdAsync(this, ct).ConfigureAwait(false);
        var vendors = await GetRawListAsync<BcVendor>($"companies({companyId})/vendors?$top=1&$select=number", ct).ConfigureAwait(false);
        var o = options.Value;
        return $"Connected to Business Central environment '{o.Environment}', company '{o.CompanyName}' ({companyId}). " +
               (vendors.Count == 0 ? "No vendors were returned: check the D365 READ permission set." : "Vendor records can be read.");
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string relativeUrl, CancellationToken ct)
    {
        var companyId = await company.GetIdAsync(this, ct).ConfigureAwait(false);
        return await GetRawListAsync<T>($"companies({companyId})/{relativeUrl}", ct).ConfigureAwait(false);
    }

    /// <summary>GET relative to .../api/v2.0/ (no company segment).</summary>
    internal async Task<IReadOnlyList<T>> GetRawListAsync<T>(string relativeUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUrl, UriKind.Relative));
        // Ask BC to serve the request from a read-only replica where it has one.
        request.Headers.Add("Data-Access-Intent", "ReadOnly");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
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

    internal sealed class BcCompany
    {
        public Guid Id { get; set; }

        public string? Name { get; set; }
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

/// <summary>
/// The company's id: configured, or found once by name and then cached for the life of the app.
/// </summary>
public sealed class BusinessCentralCompany(IOptions<BusinessCentralOptions> options)
{
    // Two first requests at once may both look the name up; they get the same id, so no lock is needed.
    private volatile string? _id;

    internal async Task<string> GetIdAsync(BusinessCentralGateway gateway, CancellationToken ct)
    {
        if (_id is { } cached)
        {
            return cached;
        }

        var o = options.Value;
        if (o.HasCompanyId)
        {
            _id = o.CompanyId!.Trim();
            return _id;
        }

        var name = o.CompanyName?.Trim() ?? throw new InvalidOperationException("BusinessCentral:CompanyName or CompanyId is not configured.");
        var found = await gateway.GetRawListAsync<BusinessCentralGateway.BcCompany>(
            "companies?$filter=" + Uri.EscapeDataString($"name eq {BusinessCentralGateway.Literal(name)}"), ct).ConfigureAwait(false);
        if (found.Count != 1)
        {
            throw new InvalidOperationException($"Business Central company '{name}' was found {found.Count} times in environment '{o.Environment}'; expected exactly once.");
        }

        _id = found[0].Id.ToString();
        return _id;
    }
}

/// <summary>
/// Gets Business Central tokens without any stored secret. Singleton, so tokens are cached.
/// <para>Federated (Azure): the app's managed identity gets a token for
/// <c>api://AzureADTokenExchange</c>; the connector app registration trusts that token
/// (federated credential) and exchanges it, in the Business Central tenant, for a token
/// for the Business Central API.</para>
/// <para>Cli (local development): your own <c>az login --tenant desiconerp.com</c>.</para>
/// </summary>
public sealed class BusinessCentralTokenSource
{
    private static readonly string[] Scopes = ["https://api.businesscentral.dynamics.com/.default"];
    private static readonly string[] ExchangeScopes = ["api://AzureADTokenExchange/.default"];

    private readonly TokenCredential _credential;

    public BusinessCentralTokenSource(IOptions<BusinessCentralOptions> options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        var o = options.Value;
        var tenant = o.TenantId ?? throw new InvalidOperationException("BusinessCentral:TenantId is not configured.");

        if (o.UsesCli)
        {
            _credential = new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenant });
            return;
        }

        var miClientId = configuration["Azure:ManagedIdentityClientId"];
        var managedIdentity = string.IsNullOrWhiteSpace(miClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(miClientId));

        _credential = new ClientAssertionCredential(
            tenant,
            o.ClientId ?? throw new InvalidOperationException("BusinessCentral:ClientId is not configured."),
            async ct => (await managedIdentity.GetTokenAsync(new TokenRequestContext(ExchangeScopes), ct).ConfigureAwait(false)).Token);
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
