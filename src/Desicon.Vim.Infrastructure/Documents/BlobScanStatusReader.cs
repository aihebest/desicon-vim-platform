using Azure;
using Azure.Storage.Blobs;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Domain.Cases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Infrastructure.Documents;

/// <summary>
/// Reads the malware scan verdict that Microsoft Defender for Storage
/// ("on-upload malware scanning") writes to each blob as an index tag.
/// <list type="bullet">
/// <item>"Malware Scanning scan result" = "No threats found" → <see cref="ScanStatus.Clean"/></item>
/// <item>"Malicious" → <see cref="ScanStatus.Malicious"/></item>
/// <item>any other value (e.g. a scan error) → <see cref="ScanStatus.ScanFailed"/></item>
/// <item>tag absent (scan not finished yet) → <see cref="ScanStatus.Pending"/></item>
/// </list>
/// Reading blob index tags needs the data action
/// Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/read
/// (included in "Storage Blob Data Owner", or grant it through a custom role).
/// Azurite does not run Defender, so locally every document stays Pending.
/// </summary>
public sealed class BlobScanStatusReader(
    BlobServiceClient blobService,
    IOptions<StorageOptions> options,
    ILogger<BlobScanStatusReader> logger) : IDocumentScanReader
{
    public const string ScanResultTag = "Malware Scanning scan result";
    public const string NoThreatsFound = "No threats found";
    public const string Malicious = "Malicious";

    public async Task<ScanStatus> GetScanStatusAsync(string blobPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobPath);
        var blob = blobService.GetBlobContainerClient(options.Value.DocumentsContainer).GetBlobClient(blobPath);
        try
        {
            var tags = await blob.GetTagsAsync(cancellationToken: ct).ConfigureAwait(false);
            return Interpret(tags.Value.Tags.TryGetValue(ScanResultTag, out var value) ? value : null);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // The blob itself is missing: treat as a failed scan so a person looks at it.
            logger.LogWarning(ex, "Blob {BlobPath} not found while reading its malware scan result.", blobPath);
            return ScanStatus.ScanFailed;
        }
    }

    /// <summary>Maps the Defender tag value to a scan status (null = tag not written yet).</summary>
    public static ScanStatus Interpret(string? tagValue)
    {
        if (tagValue is null)
        {
            return ScanStatus.Pending;
        }

        if (string.Equals(tagValue, NoThreatsFound, StringComparison.OrdinalIgnoreCase))
        {
            return ScanStatus.Clean;
        }

        return string.Equals(tagValue, Malicious, StringComparison.OrdinalIgnoreCase)
            ? ScanStatus.Malicious
            : ScanStatus.ScanFailed;
    }
}
