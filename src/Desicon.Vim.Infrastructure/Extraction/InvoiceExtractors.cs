using Azure;
using Azure.AI.DocumentIntelligence;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Domain.Cases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Desicon.Vim.Infrastructure.Extraction;

/// <summary>
/// Reads suggested invoice fields with Azure AI Document Intelligence
/// ("prebuilt-invoice"). Advisory only: the Document Controller confirms every
/// value, and any failure simply returns null.
/// </summary>
public sealed class DocumentIntelligenceInvoiceExtractor(
    DocumentIntelligenceClient client,
    IOptions<DocumentIntelligenceOptions> options,
    ILogger<DocumentIntelligenceInvoiceExtractor> logger) : IInvoiceExtractor
{
    public async Task<ExtractedInvoiceFields?> ExtractAsync(Stream pdf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        try
        {
            var bytes = await BinaryData.FromStreamAsync(pdf, ct).ConfigureAwait(false);
            var operation = await client
                .AnalyzeDocumentAsync(WaitUntil.Completed, new AnalyzeDocumentOptions(options.Value.ModelId, bytes), ct)
                .ConfigureAwait(false);

            var documents = operation.Value.Documents;
            if (documents is null || documents.Count == 0)
            {
                logger.LogInformation("Document Intelligence found no invoice in the PDF.");
                return null;
            }

            return Map(documents[0].Fields);
        }
        catch (RequestFailedException ex)
        {
            logger.LogWarning(ex, "Invoice extraction failed (HTTP {Status}, {ErrorCode}); continuing without suggestions.", ex.Status, ex.ErrorCode);
            return null;
        }
    }

    internal static ExtractedInvoiceFields Map(IReadOnlyDictionary<string, DocumentField> fields)
    {
        var confidences = new List<float>();

        var vendorName = Text(fields, "VendorName", confidences);
        var invoiceNumber = Text(fields, "InvoiceId", confidences);
        var orderNumber = Text(fields, "PurchaseOrder", confidences);

        DateOnly? invoiceDate = null;
        if (fields.TryGetValue("InvoiceDate", out var dateField) && dateField?.ValueDate is { } date)
        {
            invoiceDate = DateOnly.FromDateTime(date.Date);
            Track(dateField, confidences);
        }

        decimal? amount = null;
        string? currency = null;
        if (fields.TryGetValue("InvoiceTotal", out var totalField) && totalField?.ValueCurrency is { } total)
        {
            amount = decimal.Round((decimal)total.Amount, 2);
            currency = string.IsNullOrWhiteSpace(total.CurrencyCode) ? null : total.CurrencyCode.Trim().ToUpperInvariant();
            Track(totalField, confidences);
        }

        var bankAccount = BankAccount(fields);

        double? confidence = confidences.Count == 0 ? null : confidences.Average(c => (double)c);
        return new ExtractedInvoiceFields(vendorName, invoiceNumber, invoiceDate, amount, currency, orderNumber, bankAccount, confidence);
    }

    /// <summary>
    /// Best effort. prebuilt-invoice returns "PaymentDetails" as a list of objects with
    /// "IBAN" (and "SWIFT"); Nigerian NUBAN account numbers are often not recognised at all.
    /// The value is only compared with Business Central (never written to it).
    /// </summary>
    private static string? BankAccount(IReadOnlyDictionary<string, DocumentField> fields)
    {
        if (fields.TryGetValue("PaymentDetails", out var details)
            && details?.ValueList is { Count: > 0 } list
            && list[0].ValueDictionary is { } first
            && first.TryGetValue("IBAN", out var iban)
            && iban is not null)
        {
            var value = iban.ValueString ?? iban.Content;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return null;
    }

    private static string? Text(IReadOnlyDictionary<string, DocumentField> fields, string name, List<float> confidences)
    {
        if (!fields.TryGetValue(name, out var field) || field is null)
        {
            return null;
        }

        var value = field.ValueString ?? field.Content;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Track(field, confidences);
        return value.Trim();
    }

    private static void Track(DocumentField field, List<float> confidences)
    {
        if (field.Confidence is { } c)
        {
            confidences.Add(c);
        }
    }
}

/// <summary>Used when DocumentIntelligence:Endpoint is not configured. The DC types the fields in.</summary>
public sealed class NullInvoiceExtractor : IInvoiceExtractor
{
    public Task<ExtractedInvoiceFields?> ExtractAsync(Stream pdf, CancellationToken ct) =>
        Task.FromResult<ExtractedInvoiceFields?>(null);
}
