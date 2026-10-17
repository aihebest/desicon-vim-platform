using System.Text;

namespace Desicon.Vim.Domain.Integrity;

/// <summary>The fields needed to compare one invoice with another.</summary>
public sealed record InvoiceFingerprint(
    Guid CaseId,
    string Reference,
    string? VendorNo,
    string? VendorName,
    string InvoiceNumber,
    decimal? Amount,
    string? Currency,
    DateOnly? InvoiceDate);

public enum DuplicateKind
{
    /// <summary>Same vendor and same invoice number (after normalising).</summary>
    SameInvoiceNumber = 0,

    /// <summary>Same vendor, amount and currency, dated within a few days. Catches a renumbered resend.</summary>
    SameAmountNearDate = 1,

    /// <summary>Same invoice number and amount under a different vendor record or name. Catches re-registration under another vendor.</summary>
    SameNumberAndAmountOtherVendor = 2,
}

public sealed record DuplicateMatch(Guid ExistingCaseId, string ExistingReference, DuplicateKind Kind);

/// <summary>
/// Protects against paying the same invoice twice. Matching is deliberately
/// generous: a false alarm costs the DC a minute, a missed duplicate costs money.
/// </summary>
public static class DuplicateDetector
{
    public const int NearDateWindowDays = 7;

    public static IReadOnlyList<DuplicateMatch> FindMatches(InvoiceFingerprint candidate, IEnumerable<InvoiceFingerprint> existing)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(existing);

        var number = NormaliseInvoiceNumber(candidate.InvoiceNumber);
        var matches = new List<DuplicateMatch>();

        foreach (var other in existing)
        {
            if (other.CaseId == candidate.CaseId)
            {
                continue;
            }

            var sameNumber = number.Length > 0 && NormaliseInvoiceNumber(other.InvoiceNumber) == number;
            var sameAmount = candidate.Amount is { } a && other.Amount == a
                && string.Equals(candidate.Currency, other.Currency, StringComparison.OrdinalIgnoreCase);

            if (SameVendor(candidate, other))
            {
                if (sameNumber)
                {
                    matches.Add(new DuplicateMatch(other.CaseId, other.Reference, DuplicateKind.SameInvoiceNumber));
                }
                else if (sameAmount && candidate.InvoiceDate is { } d1 && other.InvoiceDate is { } d2
                    && Math.Abs(d1.DayNumber - d2.DayNumber) <= NearDateWindowDays)
                {
                    matches.Add(new DuplicateMatch(other.CaseId, other.Reference, DuplicateKind.SameAmountNearDate));
                }
            }
            else if (sameNumber && sameAmount)
            {
                matches.Add(new DuplicateMatch(other.CaseId, other.Reference, DuplicateKind.SameNumberAndAmountOtherVendor));
            }
        }

        return matches;
    }

    /// <summary>Same BC vendor number, or the same vendor name after normalising. Either is enough.</summary>
    public static bool SameVendor(InvoiceFingerprint a, InvoiceFingerprint b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var sameNo = !string.IsNullOrWhiteSpace(a.VendorNo) && string.Equals(a.VendorNo.Trim(), b.VendorNo?.Trim(), StringComparison.OrdinalIgnoreCase);
        var nameA = NormaliseKey(a.VendorName);
        return sameNo || (nameA.Length > 0 && nameA == NormaliseKey(b.VendorName));
    }

    /// <summary>"INV-0012", "inv 12" and "INV/012" all become "INV12".</summary>
    public static string NormaliseInvoiceNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        var inDigitRun = false;
        foreach (var ch in value.ToUpperInvariant())
        {
            if (!char.IsLetterOrDigit(ch))
            {
                continue;
            }

            if (char.IsDigit(ch))
            {
                // Drop leading zeros inside each run of digits.
                if (!inDigitRun && ch == '0')
                {
                    continue;
                }

                inDigitRun = true;
            }
            else
            {
                inDigitRun = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>Vendor keys compare without case, spaces or punctuation ("ABC Ltd." = "abc ltd").</summary>
    public static string NormaliseKey(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
}
