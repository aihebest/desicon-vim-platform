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

    /// <summary>UPN or Entra object id of the mailbox, e.g. invoices@desicongroup.com. Notifications are sent from it too.</summary>
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

/// <summary>Business Central API connection, section "BusinessCentral". Read-only use only.</summary>
public sealed class BusinessCentralOptions
{
    public const string SectionName = "BusinessCentral";

    /// <summary>Master switch. Even with all settings present, nothing is called unless this is true.</summary>
    public bool Enabled { get; set; }

    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Business Central environment name, e.g. "Production" or "Sandbox".</summary>
    public string Environment { get; set; } = "Production";

    /// <summary>Company id (GUID) as returned by GET .../api/v2.0/companies.</summary>
    public string? CompanyId { get; set; }

    /// <summary>
    /// Business Central leaves currencyCode empty for the local currency (LCY).
    /// This value is used instead, so "NGN" on a case matches an LCY order.
    /// </summary>
    public string LocalCurrencyCode { get; set; } = "NGN";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(Environment)
        && !string.IsNullOrWhiteSpace(CompanyId);

    public bool IsEnabled => Enabled && IsConfigured;

    /// <summary>https://api.businesscentral.dynamics.com/v2.0/{tenant}/{environment}/api/v2.0/companies({companyId})/</summary>
    public Uri BuildBaseAddress() =>
        new($"https://api.businesscentral.dynamics.com/v2.0/{Uri.EscapeDataString(TenantId ?? string.Empty)}/{Uri.EscapeDataString(Environment)}/api/v2.0/companies({Uri.EscapeDataString(CompanyId ?? string.Empty)})/");
}
