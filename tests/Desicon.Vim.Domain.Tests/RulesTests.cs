using Desicon.Vim.Domain.Integrity;
using Desicon.Vim.Domain.Scheduling;

namespace Desicon.Vim.Domain.Tests;

public class DuplicateDetectorTests
{
    private static InvoiceFingerprint F(string reference, string vendor, string number, decimal? amount = 100m, string date = "2026-10-14", string? vendorNo = null) =>
        new(Guid.NewGuid(), reference, vendorNo, vendor, number, amount, "NGN", DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("INV-0012", "INV12")]
    [InlineData("inv 12", "INV12")]
    [InlineData("INV/012", "INV12")]
    [InlineData("2026-001", "2026001")]
    [InlineData("", "")]
    public void Invoice_numbers_are_normalised(string input, string expected) =>
        Assert.Equal(expected, DuplicateDetector.NormaliseInvoiceNumber(input));

    [Fact]
    public void Same_vendor_and_number_in_a_different_format_is_a_duplicate()
    {
        var existing = F("VIM-2026-000001", "ABC Ltd.", "INV-001");
        var candidate = F("VIM-2026-000009", "abc ltd", "inv 1", amount: 999m);

        var match = Assert.Single(DuplicateDetector.FindMatches(candidate, [existing]));
        Assert.Equal(DuplicateKind.SameInvoiceNumber, match.Kind);
        Assert.Equal("VIM-2026-000001", match.ExistingReference);
    }

    [Fact]
    public void Renumbered_resend_with_same_amount_and_near_date_is_flagged()
    {
        var existing = F("VIM-2026-000001", "ABC Ltd", "INV-001", 500_000m, "2026-10-10");
        var candidate = F("VIM-2026-000009", "ABC Ltd", "INV-777", 500_000m, "2026-10-14");

        Assert.Equal(DuplicateKind.SameAmountNearDate, Assert.Single(DuplicateDetector.FindMatches(candidate, [existing])).Kind);
    }

    [Fact]
    public void Different_vendor_with_a_different_amount_is_not_a_duplicate()
    {
        var existing = F("VIM-2026-000001", "ABC Ltd", "INV-001", 100m);
        var candidate = F("VIM-2026-000009", "XYZ Ltd", "INV-001", 250m);
        Assert.Empty(DuplicateDetector.FindMatches(candidate, [existing]));
    }

    [Fact]
    public void Same_number_and_amount_registered_under_another_vendor_name_is_flagged()
    {
        var existing = F("VIM-2026-000001", "ABC Ltd", "INV-12", 500m, vendorNo: "V0001");
        var candidate = F("VIM-2026-000009", "A.B.C. Trading", "INV 12", 500m);

        Assert.Equal(DuplicateKind.SameNumberAndAmountOtherVendor, Assert.Single(DuplicateDetector.FindMatches(candidate, [existing])).Kind);
    }

    [Fact]
    public void Same_vendor_number_with_a_different_name_is_the_same_vendor()
    {
        var existing = F("VIM-2026-000001", "ABC Ltd", "INV-12", vendorNo: "V0001");
        var candidate = F("VIM-2026-000009", "ABC Limited (Port Harcourt)", "INV-12", vendorNo: "v0001");
        Assert.Equal(DuplicateKind.SameInvoiceNumber, Assert.Single(DuplicateDetector.FindMatches(candidate, [existing])).Kind);
    }
}

public class CaseReferenceTests
{
    [Fact]
    public void Formats_and_finds_references()
    {
        Assert.Equal("VIM-2026-000123", CaseReference.Format(2026, 123));
        Assert.True(CaseReference.IsValid("VIM-2026-000123"));
        Assert.Equal("VIM-2026-000123", CaseReference.FindIn("RE: vim-2026-000123 corrected documents"));
        Assert.Null(CaseReference.FindIn("ABC LTD_INV001_PO123_14Oct26"));
    }
}

public class WorkingCalendarTests
{
    private static readonly WorkingCalendar Calendar = WorkingCalendar.Default;

    private static DateTimeOffset Wat(int month, int day, int hour, int minute = 0) =>
        new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.FromHours(1)).ToUniversalTime();

    [Fact]
    public void Registered_before_cut_off_is_due_same_day_at_4pm()
    {
        var due = Calendar.SameDayCutOff(Wat(10, 19, 10), new TimeOnly(16, 0)); // Monday 10:00
        Assert.Equal(Wat(10, 19, 16), due);
    }

    [Fact]
    public void Registered_after_cut_off_on_friday_is_due_monday_4pm()
    {
        var due = Calendar.SameDayCutOff(Wat(10, 23, 16, 30), new TimeOnly(16, 0)); // Friday 16:30
        Assert.Equal(Wat(10, 26, 16), due);
    }

    [Fact]
    public void Working_hours_skip_the_night_and_the_weekend()
    {
        // Friday 15:00 + 4 working hours = Friday 17:00 (2h) + Monday 08:00-10:00 (2h).
        var due = Calendar.AddWorkingHours(Wat(10, 23, 15), 4);
        Assert.Equal(Wat(10, 26, 10), due);
    }

    [Fact]
    public void Public_holidays_are_skipped()
    {
        var withHoliday = new WorkingCalendar(new TimeOnly(8, 0), new TimeOnly(17, 0), [new DateOnly(2026, 10, 1)]); // Independence Day (Thu)
        var due = withHoliday.AddWorkingHours(Wat(9, 30, 16), 2); // Wed 16:00 + 2h → Wed 17:00 (1h) + Fri 08:00-09:00 (1h)
        Assert.Equal(Wat(10, 2, 9), due);
    }

    [Fact]
    public void Sop_default_is_48_calendar_hours_with_a_halfway_reminder()
    {
        var start = Wat(10, 19, 10);
        var due = SlaPolicy.SopDefault.VerificationDue(start, Calendar);
        Assert.Equal(start.AddHours(48), due);
        Assert.Equal(start.AddHours(24), SlaPolicy.SopDefault.ReminderAt(start, due));
    }
}
