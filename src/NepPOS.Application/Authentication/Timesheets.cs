using System.Globalization;
using System.Text;

namespace NepPOS.Application.Authentication;

/// <summary>One clock-in to clock-out period. <see cref="ClockOutUtc"/> is null while the person is still on shift.</summary>
public sealed record ShiftRow(int Id, int EmployeeId, string EmployeeName, DateTimeOffset ClockInUtc, DateTimeOffset? ClockOutUtc, string RegisterId)
{
    public bool IsOpen => ClockOutUtc is null;

    /// <summary>Hours worked; an open shift counts up to <paramref name="nowUtc"/>.</summary>
    public double Hours(DateTimeOffset nowUtc) => Math.Max(0, ((ClockOutUtc ?? nowUtc) - ClockInUtc).TotalHours);
}

/// <summary>A person's hours for a period, and their estimated pay when an hourly rate is set.</summary>
public sealed record TimesheetLine(int EmployeeId, string EmployeeName, int Shifts, double Hours, decimal? HourlyRate, bool OnShiftNow)
{
    public decimal? EstimatedPay => HourlyRate is { } rate ? Math.Round(rate * (decimal)Hours, 2) : null;
}

/// <summary>Clock in and out at the register, and the shifts managers look at on Manage → Timesheets.</summary>
public interface IShiftService
{
    /// <summary>The person's open shift, or null when they aren't clocked in.</summary>
    Task<ShiftRow?> OpenShiftAsync(int employeeId, CancellationToken cancellationToken = default);

    Task<StaffResult> ClockInAsync(int employeeId, string registerId, CancellationToken cancellationToken = default);

    Task<StaffResult> ClockOutAsync(int employeeId, CancellationToken cancellationToken = default);

    /// <summary>Shifts that started on these local days (inclusive), newest first, plus any still open.</summary>
    Task<IReadOnlyList<ShiftRow>> ListAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>A manager fixes a shift's times, e.g. a forgotten clock-out. Null clock-out leaves it open.</summary>
    Task<StaffResult> CorrectAsync(StaffActor actor, int shiftId, DateTimeOffset clockInUtc, DateTimeOffset? clockOutUtc, CancellationToken cancellationToken = default);
}

/// <summary>Hours per person and the CSV export for payroll. Pure, so it's easy to test.</summary>
public static class Timesheet
{
    /// <summary>The longest a single shift can be; longer usually means a forgotten clock-out.</summary>
    public static readonly TimeSpan LongestShift = TimeSpan.FromHours(16);

    /// <summary>Why corrected times can't be saved, or null when they're fine.</summary>
    public static string? TimesProblem(DateTimeOffset clockInUtc, DateTimeOffset? clockOutUtc, DateTimeOffset nowUtc)
    {
        if (clockInUtc > nowUtc.AddMinutes(5))
            return "The clock-in time is in the future.";
        if (clockOutUtc is { } outUtc)
        {
            if (outUtc <= clockInUtc)
                return "Clock-out must be after clock-in.";
            if (outUtc > nowUtc.AddMinutes(5))
                return "The clock-out time is in the future.";
            if (outUtc - clockInUtc > LongestShift)
                return $"A shift can't be longer than {LongestShift.TotalHours:0} hours. Split it, or check the times.";
        }
        return null;
    }

    /// <summary>One line per person who worked, most hours first.</summary>
    public static IReadOnlyList<TimesheetLine> Summarise(IEnumerable<ShiftRow> shifts, IReadOnlyDictionary<int, decimal?> hourlyRates, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(shifts);
        ArgumentNullException.ThrowIfNull(hourlyRates);

        return shifts
            .GroupBy(s => s.EmployeeId)
            .Select(g => new TimesheetLine(
                g.Key,
                g.OrderByDescending(s => s.ClockInUtc).First().EmployeeName,
                g.Count(),
                Math.Round(g.Sum(s => s.Hours(nowUtc)), 2),
                hourlyRates.TryGetValue(g.Key, out var rate) ? rate : null,
                g.Any(s => s.IsOpen)))
            .OrderByDescending(l => l.Hours)
            .ThenBy(l => l.EmployeeName)
            .ToList();
    }

    /// <summary>
    /// Every shift as CSV for payroll, in the register's local time: name, date, in, out, hours.
    /// Open shifts have an empty clock-out and count no hours (they aren't finished).
    /// </summary>
    public static string ToCsv(IEnumerable<ShiftRow> shifts, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(shifts);
        ArgumentNullException.ThrowIfNull(zone);

        var csv = new StringBuilder("Staff member,Date,Clock in,Clock out,Hours\r\n");
        foreach (var shift in shifts.OrderBy(s => s.EmployeeName).ThenBy(s => s.ClockInUtc))
        {
            var clockIn = TimeZoneInfo.ConvertTime(shift.ClockInUtc, zone);
            var clockOut = shift.ClockOutUtc is { } o ? TimeZoneInfo.ConvertTime(o, zone) : (DateTimeOffset?)null;
            csv.Append(Escape(shift.EmployeeName)).Append(',')
               .Append(clockIn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
               .Append(clockIn.ToString("HH:mm", CultureInfo.InvariantCulture)).Append(',')
               .Append(clockOut?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "").Append(',')
               .Append(shift.IsOpen ? "" : shift.Hours(DateTimeOffset.MaxValue).ToString("0.00", CultureInfo.InvariantCulture))
               .Append("\r\n");
        }
        return csv.ToString();
    }

    // Quotes a value with a comma or quote; a leading = + - @ is neutralised so Excel doesn't run it as a formula.
    private static string Escape(string value)
    {
        var safe = value.Length > 0 && "=+-@".Contains(value[0]) ? "'" + value : value;
        return safe.Contains(',') || safe.Contains('"') ? "\"" + safe.Replace("\"", "\"\"") + "\"" : safe;
    }
}
