using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Authentication;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Infrastructure.Database.Authentication;

/// <summary>
/// Clock in / clock out, in the register's own database (works offline).
/// SQLite can't compare or sort DateTimeOffset in SQL, so time filters run in memory; a shop has a few
/// thousand shifts a year at most.
/// </summary>
public sealed class ShiftService : IShiftService
{
    private readonly WorkforceDbContext _db;
    private readonly ILogger<ShiftService> _logger;
    private readonly TimeProvider _clock;

    public ShiftService(WorkforceDbContext db, ILogger<ShiftService> logger, TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ShiftRow?> OpenShiftAsync(int employeeId, CancellationToken cancellationToken = default)
    {
        var open = await _db.Shifts.AsNoTracking().Include(s => s.Employee)
            .Where(s => s.EmployeeId == employeeId && s.ClockOutUtc == null)
            .ToListAsync(cancellationToken);
        return open.OrderByDescending(s => s.ClockInUtc).Select(Row).FirstOrDefault();
    }

    public async Task<StaffResult> ClockInAsync(int employeeId, string registerId, CancellationToken cancellationToken = default)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        if (employee is null || !employee.IsActive)
            return StaffResult.Refused("Only staff who can sign in can clock in.");
        if (await OpenShiftAsync(employeeId, cancellationToken) is { } open)
            return StaffResult.Refused($"{employee.FullName} has been clocked in since {Local(open.ClockInUtc):h:mm tt}.");

        var now = _clock.GetUtcNow();
        _db.Shifts.Add(new Shift { EmployeeId = employeeId, ClockInUtc = now, RegisterId = registerId ?? "" });
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("{Name} clocked in", employee.FullName);
        return StaffResult.Done($"{employee.FullName} clocked in at {Local(now):h:mm tt}.");
    }

    public async Task<StaffResult> ClockOutAsync(int employeeId, CancellationToken cancellationToken = default)
    {
        var open = await _db.Shifts.Include(s => s.Employee)
            .Where(s => s.EmployeeId == employeeId && s.ClockOutUtc == null)
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
            return StaffResult.Refused("You're not clocked in.");

        var now = _clock.GetUtcNow();
        foreach (var shift in open)
            shift.ClockOutUtc = now < shift.ClockInUtc ? shift.ClockInUtc : now;
        await _db.SaveChangesAsync(cancellationToken);

        var hours = open.Sum(s => (s.ClockOutUtc!.Value - s.ClockInUtc).TotalHours);
        var name = open[0].Employee.FullName;
        _logger.LogInformation("{Name} clocked out after {Hours:0.00} h", name, hours);
        return StaffResult.Done($"{name} clocked out at {Local(now):h:mm tt}: {Duration(hours)} on this shift.");
    }

    public async Task<IReadOnlyList<ShiftRow>> ListAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
            (from, to) = (to, from);

        var all = await _db.Shifts.AsNoTracking().Include(s => s.Employee).ToListAsync(cancellationToken);
        return all
            .Where(s => s.ClockOutUtc is null || InRange(DateOnly.FromDateTime(Local(s.ClockInUtc).DateTime), from, to))
            .OrderByDescending(s => s.ClockInUtc)
            .Select(Row)
            .ToList();
    }

    public async Task<StaffResult> CorrectAsync(StaffActor actor, int shiftId, DateTimeOffset clockInUtc, DateTimeOffset? clockOutUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (actor.Role < EmployeeRole.Manager)
            return StaffResult.Refused("Only a manager or owner can change shift times.");

        var shift = await _db.Shifts.Include(s => s.Employee).FirstOrDefaultAsync(s => s.Id == shiftId, cancellationToken);
        if (shift is null)
            return StaffResult.Refused("That shift wasn't found.");
        if (shift.EmployeeId != actor.Id && !StaffRules.CanManage(actor.Role, shift.Employee.Role))
            return StaffResult.Refused("Only an owner can change a manager's or owner's shifts.");
        if (shift.EmployeeId == actor.Id && actor.Role < EmployeeRole.Owner)
            return StaffResult.Refused("Ask another manager or the owner to change your own shift.");
        if (Timesheet.TimesProblem(clockInUtc, clockOutUtc, _clock.GetUtcNow()) is { } problem)
            return StaffResult.Refused(problem);

        // A second open shift for the same person would make clock-out ambiguous.
        if (clockOutUtc is null && await _db.Shifts.AnyAsync(s => s.Id != shiftId && s.EmployeeId == shift.EmployeeId && s.ClockOutUtc == null, cancellationToken))
            return StaffResult.Refused($"{shift.Employee.FullName} already has an open shift. Enter a clock-out time.");

        shift.ClockInUtc = clockInUtc;
        shift.ClockOutUtc = clockOutUtc;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Shift {Id} for {Name} corrected by staff member {Actor}", shift.Id, shift.Employee.FullName, actor.Id);
        return StaffResult.Done($"Shift saved for {shift.Employee.FullName}.");
    }

    private static ShiftRow Row(Shift s) =>
        new(s.Id, s.EmployeeId, s.Employee.FullName, s.ClockInUtc, s.ClockOutUtc, s.RegisterId);

    private static bool InRange(DateOnly day, DateOnly from, DateOnly to) => day >= from && day <= to;

    private static DateTimeOffset Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local);

    private static string Duration(double hours)
    {
        var span = TimeSpan.FromHours(hours);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{span.Minutes} min";
    }
}
