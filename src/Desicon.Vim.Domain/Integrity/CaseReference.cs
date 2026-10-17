using System.Globalization;
using System.Text.RegularExpressions;

namespace Desicon.Vim.Domain.Integrity;

/// <summary>
/// The Desicon tracking reference given to every package, e.g. VIM-2026-000123.
/// The sequence number comes from a database sequence so it is unique even
/// when two packages arrive at the same moment.
/// </summary>
public static partial class CaseReference
{
    public const string Prefix = "VIM";

    public static string Format(int year, long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 2000);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        return string.Create(CultureInfo.InvariantCulture, $"{Prefix}-{year:D4}-{sequence:D6}");
    }

    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    /// <summary>Finds a reference inside free text such as an email subject ("RE: VIM-2026-000123 corrected").</summary>
    public static string? FindIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var m = SearchPattern().Match(text);
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"^VIM-\d{4}-\d{6,}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"VIM-\d{4}-\d{6,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SearchPattern();
}
