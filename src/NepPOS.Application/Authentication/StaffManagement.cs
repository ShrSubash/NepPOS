using NepPOS.Domain.Enums;

namespace NepPOS.Application.Authentication;

/// <summary>A staff member as shown on Manage → Staff &amp; PINs. The PIN itself is never read back.</summary>
public sealed record StaffMember(int Id, string FullName, EmployeeRole Role, bool IsActive, bool UsesStartingPin);

/// <summary>Who is making the change (the signed-in manager or owner).</summary>
public sealed record StaffActor(int Id, EmployeeRole Role);

/// <summary>The outcome of a staff change, in words the manager can act on.</summary>
public sealed record StaffResult(bool Succeeded, string Message)
{
    public static StaffResult Done(string message) => new(true, message);
    public static StaffResult Refused(string message) => new(false, message);
}

/// <summary>Add staff, change their name, role and PIN, and turn sign-in off when someone leaves.</summary>
public interface IStaffService
{
    Task<IReadOnlyList<StaffMember>> ListAsync(CancellationToken cancellationToken = default);

    Task<StaffResult> AddAsync(StaffActor actor, string fullName, EmployeeRole role, string pin, CancellationToken cancellationToken = default);

    Task<StaffResult> UpdateAsync(StaffActor actor, int employeeId, string fullName, EmployeeRole role, bool isActive, CancellationToken cancellationToken = default);

    Task<StaffResult> SetPinAsync(StaffActor actor, int employeeId, string pin, CancellationToken cancellationToken = default);
}

/// <summary>
/// The rules for staff and PINs, kept apart from the database so they're easy to test.
///
/// Sign-in is by PIN alone, so every PIN must belong to one person. Staff are never deleted (their sales point at
/// them); someone who leaves has sign-in turned off. Managers look after cashiers and supervisors; only an owner
/// adds or changes managers and owners, so nobody can promote themselves. There is always at least one owner who
/// can sign in.
/// </summary>
public static class StaffRules
{
    public const int MinPinLength = 4;
    public const int MaxPinLength = 6;   // the sign-in pad takes up to 6 digits
    public const int MaxNameLength = 200;

    /// <summary>The PINs a new register starts with. Shown as a warning until they're changed.</summary>
    public static readonly IReadOnlyList<string> StartingPins = new[] { "123456", "1111" };

    /// <summary>Why a PIN can't be used, or null when it's fine.</summary>
    public static string? PinProblem(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length < MinPinLength || pin.Length > MaxPinLength || !pin.All(char.IsAsciiDigit))
            return $"A PIN is {MinPinLength} to {MaxPinLength} digits.";
        if (pin.Distinct().Count() == 1)
            return "Choose a PIN that isn't one digit repeated.";
        if (IsRun(pin, +1) || IsRun(pin, -1))
            return "Choose a PIN that isn't a run like 1234 or 4321.";
        return null;
    }

    /// <summary>Why a name can't be used, or null when it's fine.</summary>
    public static string? NameProblem(string? fullName)
    {
        var name = fullName?.Trim() ?? "";
        if (name.Length == 0) return "Enter the person's name.";
        if (name.Length > MaxNameLength) return $"Names are up to {MaxNameLength} characters.";
        return null;
    }

    /// <summary>Owners look after everyone; managers look after cashiers and supervisors.</summary>
    public static bool CanManage(EmployeeRole actor, EmployeeRole target) =>
        actor == EmployeeRole.Owner || (actor >= EmployeeRole.Manager && target < EmployeeRole.Manager);

    /// <summary>The roles this person may give to someone.</summary>
    public static IReadOnlyList<EmployeeRole> AssignableRoles(EmployeeRole actor) =>
        Enum.GetValues<EmployeeRole>().Where(role => CanManage(actor, role)).ToList();

    public static string Describe(EmployeeRole role) => role switch
    {
        EmployeeRole.Cashier => "Cashier",
        EmployeeRole.ShiftSupervisor => "Supervisor",
        EmployeeRole.Manager => "Manager",
        EmployeeRole.Owner => "Owner",
        _ => role.ToString()
    };

    private static bool IsRun(string pin, int step)
    {
        for (var i = 1; i < pin.Length; i++)
            if (pin[i] - pin[i - 1] != step)
                return false;
        return true;
    }
}
