using Desicon.Vim.Domain.Scheduling;

namespace Desicon.Vim.Application;

/// <summary>Business settings, bound from configuration section "Vim".</summary>
public sealed class VimOptions
{
    public const string SectionName = "Vim";

    /// <summary>Base URL of the workbench, used in notification links.</summary>
    public string WorkbenchUrl { get; set; } = "https://localhost:5001";

    /// <summary>Who receives escalations (SOP: Finance Manager).</summary>
    public string FinanceManagerEmail { get; set; } = string.Empty;

    /// <summary>Shared address for the AP Coordinator team.</summary>
    public string AccountsPayableEmail { get; set; } = string.Empty;

    /// <summary>Largest accepted attachment, in megabytes.</summary>
    public int MaxAttachmentMegabytes { get; set; } = 25;

    /// <summary>Hours used for review deadlines (see SlaPolicy).</summary>
    public double VerificationHours { get; set; } = SlaPolicy.SopDefault.VerificationHours;

    public SlaBasis VerificationBasis { get; set; } = SlaPolicy.SopDefault.VerificationBasis;

    /// <summary>Lagos public holidays (yyyy-MM-dd) excluded from working-hour deadlines.</summary>
    public IList<string> PublicHolidays { get; } = [];

    public SlaPolicy ToSlaPolicy() => SlaPolicy.SopDefault with
    {
        VerificationHours = VerificationHours,
        VerificationBasis = VerificationBasis,
    };

    public WorkingCalendar ToCalendar() => new(
        WorkingCalendar.Default.DayStart,
        WorkingCalendar.Default.DayEnd,
        PublicHolidays.Select(h => DateOnly.Parse(h, System.Globalization.CultureInfo.InvariantCulture)));
}
