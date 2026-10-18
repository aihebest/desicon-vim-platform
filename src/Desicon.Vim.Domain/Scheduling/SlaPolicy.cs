namespace Desicon.Vim.Domain.Scheduling;

/// <summary>Whether a deadline counts every hour or only working hours.</summary>
public enum SlaBasis
{
    CalendarHours = 0,
    WorkingHours = 1,
}

/// <summary>
/// Deadlines from SOP DEL/FIN/AP/SOP/001. Held as configuration so Finance
/// can change them. Whether "48 hours" means calendar or working hours is an
/// open question for Finance; the default below is calendar hours, which is
/// the literal reading of the SOP.
/// </summary>
public sealed record SlaPolicy(
    TimeOnly DistributionCutOff,
    double VerificationHours,
    SlaBasis VerificationBasis,
    double ReminderAtFraction)
{
    public static SlaPolicy SopDefault { get; } = new(
        DistributionCutOff: new TimeOnly(16, 0),
        VerificationHours: 48,
        VerificationBasis: SlaBasis.CalendarHours,
        ReminderAtFraction: 0.5);

    public DateTimeOffset DistributionDue(DateTimeOffset registeredUtc, WorkingCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return calendar.SameDayCutOff(registeredUtc, DistributionCutOff);
    }

    public DateTimeOffset VerificationDue(DateTimeOffset assignedUtc, WorkingCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return VerificationBasis == SlaBasis.WorkingHours
            ? calendar.AddWorkingHours(assignedUtc, VerificationHours)
            : assignedUtc.AddHours(VerificationHours);
    }

    public DateTimeOffset ReminderAt(DateTimeOffset startUtc, DateTimeOffset dueUtc) =>
        startUtc + TimeSpan.FromTicks((long)((dueUtc - startUtc).Ticks * ReminderAtFraction));
}
