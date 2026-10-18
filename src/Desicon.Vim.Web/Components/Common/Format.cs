using System.Globalization;

namespace Desicon.Vim.Web.Components.Common;

/// <summary>Display formatting. Times are shown in West Africa Time (UTC+1).</summary>
public static class Format
{
    private static readonly TimeSpan WatOffset = TimeSpan.FromHours(1);

    public static string Wat(DateTimeOffset? utc) =>
        utc is { } d ? d.ToOffset(WatOffset).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) : "—";

    public static string Money(decimal? amount, string? currency) =>
        amount is { } a ? string.Create(CultureInfo.InvariantCulture, $"{currency} {a:N2}") : "—";
}
