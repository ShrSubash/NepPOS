using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NepPOS.Application.Authentication;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Authentication;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>Manage → Staff &amp; PINs against a real SQLite database, starting from a new register's two logins.</summary>
public sealed class StaffServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<WorkforceDbContext> _options;
    private readonly StaffActor _owner;
    private readonly int _cashierId;

    public StaffServiceTests()
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

    private (StaffService Staff, WorkforceDbContext Db) Service()
    {
        var db = new WorkforceDbContext(_options);
        return (new StaffService(db, NullLogger<StaffService>.Instance), db);
    }

    private async Task<Employee?> SignInAsync(string pin)
    {
        await using var db = new WorkforceDbContext(_options);
        return (await new PinAuthenticationService(db, NullLogger<PinAuthenticationService>.Instance).AuthenticateByPinAsync(pin)).Employee;
    }

    [Fact]
    public async Task New_register_flags_both_starting_pins()
    {
        var (staff, db) = Service();
        await using var _ = db;

        Assert.All(await staff.ListAsync(), person => Assert.True(person.UsesStartingPin));
    }

    [Fact]
    public async Task Changing_the_owner_pin_signs_in_with_the_new_pin_only()
    {
        var (staff, db) = Service();
        await using var _ = db;

        var result = await staff.SetPinAsync(_owner, _owner.Id, "482613");

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(await SignInAsync("123456"));
        Assert.Equal(_owner.Id, (await SignInAsync("482613"))!.Id);
        Assert.False((await staff.ListAsync()).Single(p => p.Id == _owner.Id).UsesStartingPin);
    }

    [Fact]
    public async Task Added_staff_sign_in_and_pins_never_clash()
    {
        var (staff, db) = Service();
        await using var _ = db;

        Assert.True((await staff.AddAsync(_owner, new StaffProfile("  Sita Gurung ", EmployeeRole.Cashier), "5807")).Succeeded);
        Assert.Equal("Sita Gurung", (await SignInAsync("5807"))!.FullName);

        var clash = await staff.AddAsync(_owner, new StaffProfile("Ram Thapa", EmployeeRole.Cashier), "5807");
        Assert.False(clash.Succeeded);
        Assert.False((await staff.AddAsync(_owner, new StaffProfile("Ram Thapa", EmployeeRole.Cashier), "1111")).Succeeded);   // starting PIN
    }

    [Fact]
    public async Task Turning_sign_in_off_stops_the_pin_working()
    {
        var (staff, db) = Service();
        await using var _ = db;

        var result = await staff.UpdateAsync(_owner, _cashierId, new StaffProfile("Test Cashier", EmployeeRole.Cashier), isActive: false);

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(await SignInAsync("1111"));
    }

    [Fact]
    public async Task Managers_cannot_promote_or_touch_owners()
    {
        var (staff, db) = Service();
        await using var _ = db;
        Assert.True((await staff.AddAsync(_owner, new StaffProfile("Hari Manager", EmployeeRole.Manager), "7351")).Succeeded);
        var manager = new StaffActor((await staff.ListAsync()).Single(p => p.FullName == "Hari Manager").Id, EmployeeRole.Manager);

        Assert.False((await staff.UpdateAsync(manager, _cashierId, new StaffProfile("Test Cashier", EmployeeRole.Manager), true)).Succeeded);
        Assert.False((await staff.SetPinAsync(manager, _owner.Id, "9026")).Succeeded);
        Assert.False((await staff.UpdateAsync(manager, manager.Id, new StaffProfile("Hari Manager", EmployeeRole.Owner), true)).Succeeded);
        Assert.True((await staff.SetPinAsync(manager, _cashierId, "9026")).Succeeded);
        Assert.True((await staff.SetPinAsync(manager, manager.Id, "3519")).Succeeded);   // their own
    }

    [Fact]
    public async Task There_is_always_an_owner()
    {
        var (staff, db) = Service();
        await using var _ = db;
        Assert.True((await staff.AddAsync(_owner, new StaffProfile("Second Owner", EmployeeRole.Owner), "8264")).Succeeded);
        var second = new StaffActor((await staff.ListAsync()).Single(p => p.FullName == "Second Owner").Id, EmployeeRole.Owner);

        Assert.True((await staff.UpdateAsync(second, _owner.Id, new StaffProfile("Store Owner", EmployeeRole.Owner), isActive: false)).Succeeded);
        Assert.False((await staff.UpdateAsync(second, second.Id, new StaffProfile("Second Owner", EmployeeRole.Manager), true)).Succeeded);   // own role
    }

    [Fact]
    public async Task The_last_owner_cannot_be_turned_off_or_demoted()
    {
        var (staff, db) = Service();
        await using var _ = db;
        Assert.True((await staff.AddAsync(_owner, new StaffProfile("Second Owner", EmployeeRole.Owner), "8264")).Succeeded);
        var second = new StaffActor((await staff.ListAsync()).Single(p => p.FullName == "Second Owner").Id, EmployeeRole.Owner);
        Assert.True((await staff.UpdateAsync(_owner, second.Id, new StaffProfile("Second Owner", EmployeeRole.Owner), isActive: false)).Succeeded);

        // Second owner is off: the remaining owner can't be demoted by the (now inactive) second owner.
        var result = await staff.UpdateAsync(second, _owner.Id, new StaffProfile("Store Owner", EmployeeRole.Manager), true);
        Assert.False(result.Succeeded);
        Assert.Contains("always be an owner", result.Message);
    }

    [Fact]
    public async Task Contact_and_pay_details_are_saved_and_checked()
    {
        var (staff, db) = Service();
        await using var _ = db;

        var added = await staff.AddAsync(_owner, new StaffProfile("Sita Gurung", EmployeeRole.Cashier, " 0412 345 678 ", "sita@example.com", new DateOnly(2026, 11, 2), 28.50m, "🌸"), "5807");
        Assert.True(added.Succeeded, added.Message);
        var sita = (await staff.ListAsync()).Single(p => p.FullName == "Sita Gurung");
        Assert.Equal(("0412 345 678", "sita@example.com", new DateOnly(2026, 11, 2), 28.50m, "🌸"), (sita.Phone, sita.Email, sita.StartDate, sita.HourlyRate, sita.Avatar));

        Assert.False((await staff.UpdateAsync(_owner, sita.Id, new StaffProfile("Sita Gurung", EmployeeRole.Cashier, Email: "not-an-email"), true)).Succeeded);
        Assert.False((await staff.UpdateAsync(_owner, sita.Id, new StaffProfile("Sita Gurung", EmployeeRole.Cashier, HourlyRate: -1), true)).Succeeded);
        Assert.True((await staff.UpdateAsync(_owner, sita.Id, new StaffProfile("Sita Gurung", EmployeeRole.Cashier, Phone: "  "), true)).Succeeded);
        Assert.Null((await staff.ListAsync()).Single(p => p.Id == sita.Id).Phone);
    }

    public void Dispose() => _connection.Dispose();
}
