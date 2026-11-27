using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Authentication;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Infrastructure.Database.Authentication;

/// <summary>Staff and their PINs, in the register's own database (works offline). See <see cref="StaffRules"/>.</summary>
public sealed class StaffService : IStaffService
{
    private readonly WorkforceDbContext _db;
    private readonly ILogger<StaffService> _logger;

    public StaffService(WorkforceDbContext db, ILogger<StaffService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<StaffMember>> ListAsync(CancellationToken cancellationToken = default)
    {
        var staff = await _db.Employees.AsNoTracking().ToListAsync(cancellationToken);
        return staff
            .OrderByDescending(e => e.IsActive)
            .ThenByDescending(e => e.Role)
            .ThenBy(e => e.FullName)
            .Select(e => new StaffMember(e.Id, e.FullName, e.Role, e.IsActive,
                e.IsActive && StaffRules.StartingPins.Any(pin => PinHasher.Verify(pin, e.PinSalt, e.PinHash)),
                e.Phone, e.Email, e.StartDate, e.HourlyRate, e.Avatar))
            .ToList();
    }

    public async Task<StaffResult> AddAsync(StaffActor actor, StaffProfile profile, string pin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(profile);
        var (fullName, role) = (profile.FullName, profile.Role);

        if (!StaffRules.CanManage(actor.Role, role))
            return StaffResult.Refused($"Only an owner can add a {StaffRules.Describe(role).ToLowerInvariant()}.");
        if (StaffRules.NameProblem(fullName) is { } nameProblem)
            return StaffResult.Refused(nameProblem);
        if (StaffRules.DetailsProblem(profile) is { } detailsProblem)
            return StaffResult.Refused(detailsProblem);
        if (StaffRules.PinProblem(pin) is { } pinProblem)
            return StaffResult.Refused(pinProblem);
        if (StaffRules.StartingPins.Contains(pin))
            return StaffResult.Refused("That's a starting PIN everyone knows. Choose another.");
        if (await PinTakenAsync(pin, exceptId: null, cancellationToken))
            return StaffResult.Refused("Someone already uses that PIN. Choose another.");

        var (hash, salt) = PinHasher.Hash(pin);
        var name = fullName.Trim();
        var employee = new Employee { Role = role, PinHash = hash, PinSalt = salt, IsActive = true };
        Apply(employee, profile);
        _db.Employees.Add(employee);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Staff member added: {Name} ({Role})", name, role);
        return StaffResult.Done($"{name} added. They can sign in with their PIN now.");
    }

    public async Task<StaffResult> UpdateAsync(StaffActor actor, int employeeId, StaffProfile profile, bool isActive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(profile);
        var (fullName, role) = (profile.FullName, profile.Role);

        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        if (employee is null)
            return StaffResult.Refused("That staff member wasn't found.");
        if (StaffRules.NameProblem(fullName) is { } nameProblem)
            return StaffResult.Refused(nameProblem);
        if (StaffRules.DetailsProblem(profile) is { } detailsProblem)
            return StaffResult.Refused(detailsProblem);

        var self = employee.Id == actor.Id;
        if (self && role != employee.Role)
            return StaffResult.Refused("You can't change your own role. Ask an owner.");
        if (self && !isActive)
            return StaffResult.Refused("You can't turn off your own sign-in.");
        if (!self && (!StaffRules.CanManage(actor.Role, employee.Role) || !StaffRules.CanManage(actor.Role, role)))
            return StaffResult.Refused("Only an owner can change managers and owners.");

        var losesOwner = employee.Role == EmployeeRole.Owner && employee.IsActive && (role != EmployeeRole.Owner || !isActive);
        if (losesOwner && !await _db.Employees.AnyAsync(e => e.Id != employee.Id && e.Role == EmployeeRole.Owner && e.IsActive, cancellationToken))
            return StaffResult.Refused("There must always be an owner who can sign in. Make someone else an owner first.");

        Apply(employee, profile);
        employee.Role = role;
        employee.IsActive = isActive;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Staff member {Id} saved: {Name} ({Role}, {State})", employee.Id, employee.FullName, role, isActive ? "active" : "off");
        return StaffResult.Done(isActive ? $"{employee.FullName} saved." : $"{employee.FullName} can no longer sign in. Their past sales stay in reports.");
    }

    public async Task<StaffResult> SetPinAsync(StaffActor actor, int employeeId, string pin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        if (employee is null)
            return StaffResult.Refused("That staff member wasn't found.");
        if (employee.Id != actor.Id && !StaffRules.CanManage(actor.Role, employee.Role))
            return StaffResult.Refused("Only an owner can change a manager's or owner's PIN.");
        if (StaffRules.PinProblem(pin) is { } pinProblem)
            return StaffResult.Refused(pinProblem);
        if (StaffRules.StartingPins.Contains(pin))
            return StaffResult.Refused("That's a starting PIN everyone knows. Choose another.");
        if (await PinTakenAsync(pin, exceptId: employee.Id, cancellationToken))
            return StaffResult.Refused("Someone already uses that PIN. Choose another.");

        (employee.PinHash, employee.PinSalt) = PinHasher.Hash(pin);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("PIN changed for staff member {Id}", employee.Id);
        return StaffResult.Done($"PIN changed for {employee.FullName}.");
    }

    private static void Apply(Employee employee, StaffProfile profile)
    {
        employee.FullName = profile.FullName.Trim();
        employee.Phone = Blank(profile.Phone);
        employee.Email = Blank(profile.Email);
        employee.StartDate = profile.StartDate;
        employee.HourlyRate = profile.HourlyRate;
        employee.Avatar = Blank(profile.Avatar);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // PINs are salted hashes, so the only way to find a clash is to try the PIN against each person.
    // Inactive staff count too, so turning someone back on can never create two people with one PIN.
    private async Task<bool> PinTakenAsync(string pin, int? exceptId, CancellationToken cancellationToken)
    {
        var others = await _db.Employees.AsNoTracking()
            .Where(e => exceptId == null || e.Id != exceptId)
            .Select(e => new { e.PinHash, e.PinSalt })
            .ToListAsync(cancellationToken);
        return others.Any(e => PinHasher.Verify(pin, e.PinSalt, e.PinHash));
    }
}
