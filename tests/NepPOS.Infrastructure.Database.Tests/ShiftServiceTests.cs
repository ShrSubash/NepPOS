using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NepPOS.Application.Authentication;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Authentication;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>Clock in / clock out and a manager's corrections, against a real SQLite database.</summary>
public sealed class ShiftServiceTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<WorkforceDbContext> _options;
    private readonly Clock _clock = new();
    private readonly StaffActor _owner;
    private readonly int _cashierId;

    public ShiftServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<WorkforceDbContext>().UseSqlite(_connection).Options;

        using var db = new WorkforceDbContext(_options);
        db.Database.EnsureCreated();
        Seeding.WorkforceDbSeeder.SeedAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        _owner = new StaffActor(db.Employees.Single(e => e.Role == EmployeeRole.Owner).Id, EmployeeRole.Owner);
        _cashierId = db.Employees.Single(e => e.Role == EmployeeRole.Cashier).Id;
    }

    private ShiftService Service() => new(new WorkforceDbContext(_options), NullLogger<ShiftService>.Instance, _clock);

    private static DateOnly Day(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local).DateTime);

    [Fact]
    public async Task Clock_in_then_out_records_one_shift_with_its_hours()
    {
        Assert.True((await Service().ClockInAsync(_cashierId, "REG-001")).Succeeded);
        Assert.NotNull(await Service().OpenShiftAsync(_cashierId));
        Assert.False((await Service().ClockInAsync(_cashierId, "REG-001")).Succeeded);   // already on shift

        _clock.Now = _clock.Now.AddHours(7.5);
        Assert.True((await Service().ClockOutAsync(_cashierId)).Succeeded);

        Assert.Null(await Service().OpenShiftAsync(_cashierId));
        var shift = Assert.Single(await Service().ListAsync(Day(_clock.Now.AddDays(-1)), Day(_clock.Now)));
        Assert.Equal(7.5, shift.Hours(_clock.Now), 3);
        Assert.False((await Service().ClockOutAsync(_cashierId)).Succeeded);   // not clocked in any more
    }

    [Fact]
    public async Task Staff_who_cannot_sign_in_cannot_clock_in()
    {
        await using (var db = new WorkforceDbContext(_options))
        {
            db.Employees.Single(e => e.Id == _cashierId).IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.False((await Service().ClockInAsync(_cashierId, "REG-001")).Succeeded);
    }

    [Fact]
    public async Task A_manager_fixes_a_forgotten_clock_out()
    {
        await Service().ClockInAsync(_cashierId, "REG-001");
        var start = _clock.Now;
        _clock.Now = _clock.Now.AddHours(20);   // went home without clocking out
        var shift = (await Service().OpenShiftAsync(_cashierId))!;

        Assert.False((await Service().CorrectAsync(_owner, shift.Id, start, start.AddHours(-1))).Succeeded);   // out before in
        Assert.False((await Service().CorrectAsync(_owner, shift.Id, start, start.AddHours(18))).Succeeded);   // longer than 16 h
        Assert.False((await Service().CorrectAsync(new StaffActor(_cashierId, EmployeeRole.Cashier), shift.Id, start, start.AddHours(8))).Succeeded);

        var result = await Service().CorrectAsync(_owner, shift.Id, start, start.AddHours(8));
        Assert.True(result.Succeeded, result.Message);
        Assert.Null(await Service().OpenShiftAsync(_cashierId));
    }

    public void Dispose() => _connection.Dispose();
}
