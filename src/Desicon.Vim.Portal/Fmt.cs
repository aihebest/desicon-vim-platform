using System.Globalization;

namespace Desicon.Vim.Portal;

/// <summary>Dates shown in West Africa Time (UTC+1), the same as the workbench and emails.</summary>
public static class Fmt
{
    private static readonly TimeSpan WatOffset = TimeSpan.FromHours(1);

    public static string Wat(DateTimeOffset? utc) =>
        utc is { } d ? d.ToOffset(WatOffset).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) : "—";

    public static string Day(DateTimeOffset utc) => utc.ToOffset(WatOffset).ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string Day(DateOnly? d) => d?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "—";

    public static string Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Money(decimal? amount) => amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty;
}
