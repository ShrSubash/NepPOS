namespace NepPOS.Contracts.Admin;

// What NepPOS Portal reads from and sends to Head Office's admin API (/api/v1/admin, X-Admin-Key).
// Shared by both, so a field added on one side can't be missed on the other (docs/portal/README.md).

/// <summary>The header the Portal sends with every admin call: who is signed in, for the audit log.</summary>
public static class PortalHeaders
{
    public const string User = "X-Portal-User";
}

/// <summary>Staff roles in the Portal. Owner can do everything; Support can't manage staff.</summary>
public static class StaffRoles
{
    public const string Owner = "Owner";
    public const string Support = "Support";

    public static bool IsValid(string? role) => role is Owner or Support;
}

/// <summary>
/// The Overview page's numbers. Online = a heartbeat in the last 10 minutes; offline = not revoked and
/// silent for longer; activation codes waiting = issued, not used and not expired.
/// </summary>
public sealed record AdminOverview(
    int Businesses,
    int Stores,
    int Registers,
    int OnlineRegisters,
    int OfflineRegisters,
    int ActivationCodesWaiting,
    IReadOnlyList<RegisterRow> RegistersNeedingAttention,
    IReadOnlyList<AuditEntryRow> RecentActivity);

/// <summary>One business in the Businesses list.</summary>
public sealed record BusinessRow(
    Guid Id,
    string Name,
    DateTime CreatedUtc,
    int Stores,
    int Registers,
    int OnlineRegisters,
    DateTime? LastSeenUtc);

/// <summary>A business with everything under it.</summary>
public sealed record BusinessDetail(
    Guid Id,
    string Name,
    DateTime CreatedUtc,
    IReadOnlyList<StoreDetail> Stores,
    IReadOnlyList<WebsiteKeyRow> WebsiteKeys);

public sealed record StoreDetail(
    Guid Id,
    string StoreCode,
    string Name,
    string TimeZoneId,
    DateTime CreatedUtc,
    IReadOnlyList<RegisterRow> Registers);

/// <summary>A register anywhere in the fleet. Online = a heartbeat in the last 10 minutes.</summary>
public sealed record RegisterRow(
    Guid Id,
    Guid BusinessId,
    string BusinessName,
    Guid StoreId,
    string StoreCode,
    string RegisterCode,
    string? DeviceName,
    string ApiKeyPrefix,
    DateTime ActivatedUtc,
    DateTime? LastSeenUtc,
    bool Online,
    bool IsRevoked);

public sealed record WebsiteKeyRow(
    Guid Id,
    string Name,
    string ApiKeyPrefix,
    DateTime CreatedUtc,
    DateTime? LastUsedUtc,
    bool IsRevoked);

/// <summary>One line in the audit log: who did what, to which business, when.</summary>
public sealed record AuditEntryRow(
    long Id,
    DateTime AtUtc,
    string Actor,
    string Action,
    Guid? BusinessId,
    string? BusinessName,
    string Summary);

public sealed record RenameBusinessRequest(string Name);

// ---- Staff (people who sign in to the Portal) ----

public sealed record StaffRow(
    Guid Id,
    string Email,
    string Name,
    string Role,
    bool TwoFactorEnabled,
    bool IsActive,
    DateTime? LastSignInUtc);

public sealed record CreateStaffRequest(string Email, string Name, string Role, string Password);

/// <summary>Sign-in check. Code = the 6-digit code from an authenticator app, once set up.</summary>
public sealed record StaffSignInRequest(string Email, string Password, string? Code);

/// <summary>
/// The signed-in person. NeedsTwoFactorSetup = they must set up an authenticator app before they
/// can use the Portal.
/// </summary>
public sealed record StaffSignInResponse(Guid Id, string Email, string Name, string Role, bool NeedsTwoFactorSetup);

/// <summary>Why a sign-in was refused, so the Portal can ask for the right thing next.</summary>
public static class SignInProblems
{
    /// <summary>Password right; ask for the 6-digit code.</summary>
    public const string CodeRequired = "code_required";
    public const string Invalid = "invalid";
    public const string Locked = "locked";
}

/// <summary>A new authenticator secret, shown once as a QR code and as text.</summary>
public sealed record TwoFactorSetup(string Secret, string OtpAuthUri);

public sealed record TwoFactorConfirmRequest(string Code);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
