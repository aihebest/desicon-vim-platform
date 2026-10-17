using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Domain.Integrity;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Infrastructure.Documents;

/// <summary>
/// Stores case documents in Azure Blob Storage at
/// <c>{reference}/round-{n}/{guid}-{safeFileName}</c>. Blobs are never
/// overwritten (If-None-Match: *), and the SHA-256 of the content is recorded
/// on the case and in blob metadata so any later change is detectable.
/// </summary>
public sealed class BlobDocumentStore(BlobServiceClient blobService, IOptions<StorageOptions> options) : IDocumentStore
{
    private const int MaxFileNameLength = 100;

    private int _containerChecked;

    public async Task<StoredDocument> UploadAsync(string caseReference, int round, string fileName, string contentType, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!CaseReference.IsValid(caseReference))
        {
            throw new ArgumentException($"'{caseReference}' is not a valid case reference.", nameof(caseReference));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(round, 1);

        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"{caseReference}/round-{round}/{Guid.NewGuid():N}-{SafeFileName(fileName)}");

        // Attachments are capped (Vim:MaxAttachmentMegabytes, default 25 MB), so buffering in memory is acceptable
        // and lets us hash the exact bytes that are uploaded.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var sha256 = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
        buffer.Position = 0;

        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        var blob = container.GetBlobClient(path);
        await blob.UploadAsync(
            buffer,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType },
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sha256"] = sha256,
                    ["caseReference"] = caseReference,
                },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            },
            ct).ConfigureAwait(false);

        return new StoredDocument(path, sha256, buffer.Length);
    }

    public async Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobPath);
        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        return await container.GetBlobClient(blobPath)
            .OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps letters, digits, '.', '-' and '_' only, so a vendor-supplied name can never
    /// add path segments or odd characters to the blob path. Keeps the extension when truncating.
    /// </summary>
    public static string SafeFileName(string? fileName)
    {
        var name = (fileName ?? string.Empty).Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        }

        var safe = sb.ToString().Trim('.', '_');
        if (safe.Length == 0)
        {
            safe = "document";
        }

        if (safe.Length > MaxFileNameLength)
        {
            var extension = Path.GetExtension(safe);
            if (extension.Length > 10)
            {
                extension = string.Empty;
            }

            safe = safe[..(MaxFileNameLength - extension.Length)] + extension;
        }

        return safe;
    }

    private async Task<BlobContainerClient> GetContainerAsync(CancellationToken ct)
    {
        var container = blobService.GetBlobContainerClient(options.Value.DocumentsContainer);

        // In Azure the container is created by Terraform (with immutability/retention policies).
        // Only for local Azurite do we create it on first use.
        if (options.Value.UsesConnectionString && Interlocked.CompareExchange(ref _containerChecked, 1, 0) == 0)
        {
            await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct).ConfigureAwait(false);
        }

        return container;
    }
}
