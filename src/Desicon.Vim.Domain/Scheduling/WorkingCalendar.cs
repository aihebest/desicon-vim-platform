namespace Desicon.Vim.Domain.Scheduling;

/// <summary>
/// Desicon's working week, used to calculate review deadlines. Lagos time is
/// UTC+1 with no daylight saving, so a fixed offset is safe and avoids
/// depending on the server's time-zone database.
/// </summary>
public sealed class WorkingCalendar
{
    public static readonly TimeSpan LagosOffset = TimeSpan.FromHours(1);

    private readonly HashSet<DateOnly> _holidays;

    public WorkingCalendar(TimeOnly dayStart, TimeOnly dayEnd, IEnumerable<DateOnly>? publicHolidays = null)
    {
        if (dayEnd <= dayStart)
        {
            throw new ArgumentException("Working day must end after it starts.", nameof(dayEnd));
        }

        DayStart = dayStart;
        DayEnd = dayEnd;
        _holidays = publicHolidays?.ToHashSet() ?? [];
    }

    /// <summary>08:00–17:00 Monday to Friday, no holidays loaded.</summary>
    public static WorkingCalendar Default { get; } = new(new TimeOnly(8, 0), new TimeOnly(17, 0));

    public TimeOnly DayStart { get; }

    public TimeOnly DayEnd { get; }

    public static DateTimeOffset ToLagos(DateTimeOffset utc) => utc.ToOffset(LagosOffset);

    public bool IsWorkingDay(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !_holidays.Contains(date);

    public DateOnly NextWorkingDay(DateOnly date)
    {
        var d = date.AddDays(1);
        while (!IsWorkingDay(d))
        {
            d = d.AddDays(1);
        }

        return d;
    }

    /// <summary>
    /// SOP 7.3: the DC forwards same day by a cut-off time. If the package is
    /// registered after the cut-off, or on a non-working day, the deadline is
    /// the cut-off on the next working day.
    /// </summary>
    public DateTimeOffset SameDayCutOff(DateTimeOffset fromUtc, TimeOnly cutOff)
    {
        var local = ToLagos(fromUtc);
        var date = DateOnly.FromDateTime(local.DateTime);
        if (!IsWorkingDay(date) || TimeOnly.FromDateTime(local.DateTime) >= cutOff)
        {
            date = NextWorkingDay(date);
        }

        return new DateTimeOffset(date.ToDateTime(cutOff), LagosOffset).ToUniversalTime();
    }

    /// <summary>Adds working hours, skipping nights, weekends and holidays.</summary>
    public DateTimeOffset AddWorkingHours(DateTimeOffset fromUtc, double hours)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hours);
        var local = ToLagos(fromUtc).DateTime;
        var remaining = TimeSpan.FromHours(hours);

        // Move the starting point into working time.
        local = AlignToWorkingTime(local);

        while (remaining > TimeSpan.Zero)
        {
            var endOfDay = local.Date + DayEnd.ToTimeSpan();
            var available = endOfDay - local;
            if (remaining <= available)
            {
                local += remaining;
                remaining = TimeSpan.Zero;
            }
            else
            {
                remaining -= available;
                var next = NextWorkingDay(DateOnly.FromDateTime(local));
                local = next.ToDateTime(DayStart);
            }
        }

        return new DateTimeOffset(local, LagosOffset).ToUniversalTime();
    }

    private DateTime AlignToWorkingTime(DateTime local)
    {
        var date = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);
        if (IsWorkingDay(date) && time >= DayStart && time < DayEnd)
        {
            return local;
        }

        if (IsWorkingDay(date) && time < DayStart)
        {
            return date.ToDateTime(DayStart);
        }

        return NextWorkingDay(date).ToDateTime(DayStart);
    }
}
