using System.Diagnostics.CodeAnalysis;

namespace Desicon.Vim.Infrastructure;

/// <summary>
/// Document storage settings, section "Storage". In Azure the app uses its
/// managed identity against <see cref="BlobServiceUri"/>; a connection string
/// is only for Azurite on a developer machine.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Local development only (Azurite), e.g. "UseDevelopmentStorage=true". Leave empty in Azure.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Blob endpoint, e.g. https://stvimprod.blob.core.windows.net/ (app setting Storage__BlobServiceUri).</summary>
    public string? BlobServiceUri { get; set; }

    /// <summary>Container holding case documents (app setting Storage__DocumentsContainer).</summary>
    public string DocumentsContainer { get; set; } = "case-documents";

    [MemberNotNullWhen(true, nameof(ConnectionString))]
    public bool UsesConnectionString => !string.IsNullOrWhiteSpace(ConnectionString);
}

/// <summary>The shared invoice mailbox, section "Mailbox".</summary>
public sealed class MailboxOptions
{
    public const string SectionName = "Mailbox";

    /// <summary>UPN or Entra object id of the mailbox, e.g. invoices@example.com. Notifications are sent from it too.</summary>
    public string? UserId { get; set; }

    /// <summary>Folder (directly under the mailbox root) that processed messages are moved to. Created if missing.</summary>
    public string ProcessedFolderName { get; set; } = "Processed";
}

/// <summary>Document Intelligence, section "DocumentIntelligence". Extraction is switched off when Endpoint is empty.</summary>
public sealed class DocumentIntelligenceOptions
{
    public const string SectionName = "DocumentIntelligence";

    public string? Endpoint { get; set; }

    public string ModelId { get; set; } = "prebuilt-invoice";
}

/// <summary>
/// Business Central API connection, section "BusinessCentral". Read-only use only.
/// Business Central lives in its own Entra tenant (desiconerp.com), separate from the
/// tenant the platform runs in. The platform signs in with NO stored secret: the app's
/// managed identity is trusted by a multi-tenant "connector" app registration (a
/// federated credential), and that connector is consented in the BC tenant and enabled
/// in BC with a read-only permission set. Same pattern as the Cost Control platform (DCCP).
/// </summary>
public sealed class BusinessCentralOptions
{
    public const string SectionName = "BusinessCentral";

    /// <summary>Master switch. Even with all settings present, nothing is called unless this is true.</summary>
    public bool Enabled { get; set; }

    /// <summary>The Business Central tenant: its domain (desiconerp.com) or id.</summary>
    public string? TenantId { get; set; }

    /// <summary>Client id of the connector app registration (infra/scripts/bootstrap-bc-connector.ps1).</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// "Federated" (default; in Azure: managed identity, then the connector) or
    /// "Cli" (local development: your own <c>az login --tenant desiconerp.com</c>).
    /// </summary>
    public string AuthMode { get; set; } = "Federated";

    /// <summary>Business Central environment name, e.g. "Production" or "Sandbox".</summary>
    public string Environment { get; set; } = "Production";

    /// <summary>Company id (GUID) as returned by GET .../api/v2.0/companies. Optional when <see cref="CompanyName"/> is set.</summary>
    public string? CompanyId { get; set; }

    /// <summary>Company name, e.g. "Desicon Engineering Ltd". Looked up once to find its id.</summary>
    public string? CompanyName { get; set; }

    /// <summary>
    /// Business Central leaves currencyCode empty for the local currency (LCY).
    /// This value is used instead, so "NGN" on a case matches an LCY order.
    /// </summary>
    public string LocalCurrencyCode { get; set; } = "NGN";

    public bool UsesCli => string.Equals(AuthMode, "Cli", StringComparison.OrdinalIgnoreCase);

    public bool HasCompanyId => Guid.TryParse(CompanyId, out var id) && id != Guid.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId)
        && (UsesCli || (Guid.TryParse(ClientId, out var client) && client != Guid.Empty))
        && !string.IsNullOrWhiteSpace(Environment)
        && (HasCompanyId || !string.IsNullOrWhiteSpace(CompanyName));

    public bool IsEnabled => Enabled && IsConfigured;

    /// <summary>https://api.businesscentral.dynamics.com/v2.0/{tenant}/{environment}/api/v2.0/</summary>
    public Uri BuildBaseAddress() =>
        new($"https://api.businesscentral.dynamics.com/v2.0/{Uri.EscapeDataString(TenantId ?? string.Empty)}/{Uri.EscapeDataString(Environment)}/api/v2.0/");
}
