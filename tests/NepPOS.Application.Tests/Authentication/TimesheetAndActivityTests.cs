using NepPOS.Application.Authentication;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Authentication;

public class TimesheetAndActivityTests
{
    private static readonly DateTimeOffset Nine = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    private static ShiftRow Shift(int id, int employee, string name, double startHours, double? lengthHours) =>
        new(id, employee, name, Nine.AddHours(startHours), lengthHours is { } h ? Nine.AddHours(startHours + h) : null, "REG-001");

    [Fact]
    public void Summarise_adds_hours_per_person_and_estimates_pay()
    {
        var shifts = new[] { Shift(1, 1, "Sita", 0, 8), Shift(2, 1, "Sita", 24, 4.5), Shift(3, 2, "Ram", 0, 6) };
        var rates = new Dictionary<int, decimal?> { [1] = 30m, [2] = null };

        var lines = Timesheet.Summarise(shifts, rates, Nine.AddDays(3));

        Assert.Equal(("Sita", 2, 12.5, 375m), (lines[0].EmployeeName, lines[0].Shifts, lines[0].Hours, lines[0].EstimatedPay!.Value));
        Assert.Equal(("Ram", 6.0), (lines[1].EmployeeName, lines[1].Hours));
        Assert.Null(lines[1].EstimatedPay);
    }

    [Fact]
    public void An_open_shift_counts_up_to_now_and_shows_on_shift()
    {
        var line = Assert.Single(Timesheet.Summarise(new[] { Shift(1, 1, "Sita", 0, null) }, new Dictionary<int, decimal?>(), Nine.AddHours(3)));
        Assert.Equal(3, line.Hours);
        Assert.True(line.OnShiftNow);
    }

    [Fact]
    public void Csv_lists_each_shift_and_neutralises_formulas()
    {
        var csv = Timesheet.ToCsv(new[] { Shift(1, 1, "=Sita, G", 0, 7.25), Shift(2, 2, "Ram", 0, null) }, TimeZoneInfo.Utc);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Staff member,Date,Clock in,Clock out,Hours", lines[0]);
        Assert.Equal("\"'=Sita, G\",2026-11-23,09:00,16:15,7.25", lines[1]);
        Assert.Equal("Ram,2026-11-23,09:00,,", lines[2]);
    }

    [Fact]
    public void Corrected_times_must_make_sense()
    {
        Assert.Null(Timesheet.TimesProblem(Nine, Nine.AddHours(8), Nine.AddDays(1)));
        Assert.NotNull(Timesheet.TimesProblem(Nine, Nine.AddHours(-1), Nine.AddDays(1)));
        Assert.NotNull(Timesheet.TimesProblem(Nine, Nine.AddHours(17), Nine.AddDays(1)));
        Assert.NotNull(Timesheet.TimesProblem(Nine.AddDays(2), null, Nine.AddDays(1)));
    }

    private static long _seq;
    private static JournalEntry Entry(int employee, string name, JournalEventType type, decimal? amount = null) =>
        new(++_seq, "STORE-001", "REG-001", employee, name, type, "", amount: amount);

    [Fact]
    public void Activity_counts_what_each_person_did()
    {
        var entries = new[]
        {
            Entry(1, "Sita", JournalEventType.SignIn),
            Entry(1, "Sita", JournalEventType.SaleCompleted, 20m),
            Entry(1, "Sita", JournalEventType.SaleCompleted, 30m),
            Entry(1, "Sita", JournalEventType.RefundCompleted, -5m),
            Entry(1, "Sita", JournalEventType.NoSale),
            Entry(1, "Sita", JournalEventType.SignOut),
            Entry(2, "Ram", JournalEventType.SaleCompleted, 10m),
            Entry(2, "Ram", JournalEventType.SaleVoided, 12m),
            Entry(2, "Ram", JournalEventType.DiscountGiven, 2m),
        };

        var lines = StaffActivity.Build(entries);

        var sita = lines[0];
        Assert.Equal(("Sita", 2, 50m, 25m, 1, 5m, 1), (sita.EmployeeName, sita.Sales, sita.SalesTotal, sita.AverageSale, sita.Refunds, sita.RefundsTotal, sita.NoSales));
        Assert.NotNull(sita.FirstSignInUtc);
        Assert.NotNull(sita.LastSignOutUtc);

        var ram = lines[1];
        Assert.Equal((1, 12m, 1, 2m), (ram.Voids, ram.VoidsTotal, ram.Discounts, ram.DiscountsTotal));
        Assert.Null(ram.FirstSignInUtc);
    }
}
