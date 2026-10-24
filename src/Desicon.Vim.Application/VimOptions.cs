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

    /// <summary>Senior escalation (e.g. the DMD's office) when a Finance Manager exception is overdue. Empty: the Finance Manager only.</summary>
    public string ManagementEscalationEmail { get; set; } = string.Empty;

    /// <summary>Working days Accounts has for the confirmatory check (DMD target: 3 working days delivery-to-acceptance overall).</summary>
    public double AccountsCheckWorkingDays { get; set; } = 1;

    /// <summary>Working days the Finance Manager has to decide a flagged case.</summary>
    public double ExceptionReviewWorkingDays { get; set; } = 1;

    /// <summary>
    /// Full-value rule: the invoice must equal the value received but not yet invoiced on
    /// the PO / SO in Business Central, within this rounding tolerance (invoice currency).
    /// </summary>
    public decimal FullValueTolerance { get; set; } = 10m;

    /// <summary>If Business Central cannot be reached for this long after submission, the case goes to the Finance Manager.</summary>
    public double ErpUnavailableEscalationHours { get; set; } = 4;

    /// <summary>Legacy email intake (mailbox). Off: vendors submit on the portal (stakeholder decision, Oct 2026).</summary>
    public bool EmailIntakeEnabled { get; set; }

    /// <summary>Base URL of the vendor portal, used in invitation links. Empty: invitations cannot be sent.</summary>
    public string PortalUrl { get; set; } = string.Empty;

    /// <summary>How long an invitation link stays valid.</summary>
    public int InvitationValidityDays { get; set; } = 7;

    /// <summary>Portal users with no sign-in for this long are disabled automatically.</summary>
    public int VendorDormantDays { get; set; } = 90;

    /// <summary>
    /// Desicon's own email domains (e.g. desicongroup.com). Staff addresses can never be invited
    /// to the portal, so no employee can hold a vendor login.
    /// </summary>
    public IList<string> StaffEmailDomains { get; } = [];

    /// <summary>Lagos public holidays (yyyy-MM-dd) excluded from working-hour deadlines.</summary>
    public IList<string> PublicHolidays { get; } = [];

    public SlaPolicy ToSlaPolicy() => SlaPolicy.SopDefault with
    {
        VerificationHours = VerificationHours,
        VerificationBasis = VerificationBasis,
    };

    /// <summary>Builds stage deadlines (working days, reminder half-way in working hours).</summary>
    public StageDeadlineFactory Deadlines => new(ToCalendar());

    public WorkingCalendar ToCalendar() => new(
        WorkingCalendar.Default.DayStart,
        WorkingCalendar.Default.DayEnd,
        PublicHolidays.Select(h => DateOnly.Parse(h, System.Globalization.CultureInfo.InvariantCulture)));
}

/// <summary>Builds stage deadlines on Desicon's working calendar.</summary>
public sealed class StageDeadlineFactory(WorkingCalendar calendar)
{
    public Desicon.Vim.Domain.Cases.StageDeadline Create(string holderName, DateTimeOffset nowUtc, double workingDays)
    {
        var hours = workingDays * (calendar.DayEnd - calendar.DayStart).TotalHours;
        var due = calendar.AddWorkingHours(nowUtc, hours);
        var reminder = calendar.AddWorkingHours(nowUtc, hours / 2);
        return new Desicon.Vim.Domain.Cases.StageDeadline(holderName, due, reminder);
    }
}
