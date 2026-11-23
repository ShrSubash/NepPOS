using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NepPOS.Contracts.Admin;
using NepPOS.Contracts.Licensing;
using NepPOS.HeadOffice.Api.Data;
using NepPOS.HeadOffice.Api.Licensing;
using NepPOS.HeadOffice.Api.Security;

namespace NepPOS.HeadOffice.Api.Endpoints;

/// <summary>
/// What NepPOS Portal reads (overview, businesses, registers, audit log) and how its staff sign in.
/// Same admin key as the other admin endpoints; the Portal is the only caller (docs/portal/README.md).
/// </summary>
public static class PortalEndpoints
{
    private const int MaxFailedSignIns = 5;
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
    private static readonly PasswordHasher<PlatformUser> Hasher = new();

    public static void MapPortalEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").AddEndpointFilter<AdminKeyFilter>().WithTags("Portal");

        admin.MapGet("/overview", GetOverviewAsync);
        admin.MapGet("/tenants", ListBusinessesAsync);
        admin.MapGet("/tenants/{tenantId:guid}", GetBusinessAsync);
        admin.MapPut("/tenants/{tenantId:guid}", RenameBusinessAsync);
        admin.MapGet("/tenants/{tenantId:guid}/licence", GetLicenceAsync);
        admin.MapPut("/tenants/{tenantId:guid}/licence", UpdateLicenceAsync);
        admin.MapGet("/registers", ListRegistersAsync);
        admin.MapGet("/audit", ListAuditAsync);

        admin.MapPost("/staff/sign-in", SignInAsync);
        admin.MapGet("/staff", ListStaffAsync);
        admin.MapPost("/staff", CreateStaffAsync);
        admin.MapPost("/staff/{staffId:guid}/disable", DisableStaffAsync);
        admin.MapPost("/staff/{staffId:guid}/two-factor/start", StartTwoFactorAsync);
        admin.MapPost("/staff/{staffId:guid}/two-factor/confirm", ConfirmTwoFactorAsync);
        admin.MapPost("/staff/{staffId:guid}/password", ChangePasswordAsync);
    }

    // ---- Reading ----

    internal static async Task<IResult> GetOverviewAsync(HeadOfficeDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var registers = await RegisterRowsAsync(db, r => !r.IsRevoked, ct);
        var attention = registers.Where(r => !r.Online).OrderBy(r => r.LastSeenUtc ?? DateTime.MinValue).Take(10).ToList();

        return Results.Ok(new AdminOverview(
            Businesses: await db.Tenants.CountAsync(ct),
            Stores: await db.Stores.CountAsync(ct),
            Registers: registers.Count,
            OnlineRegisters: registers.Count(r => r.Online),
            OfflineRegisters: registers.Count(r => !r.Online),
            ActivationCodesWaiting: await db.ActivationCodes.CountAsync(a => a.UsedUtc == null && a.ExpiresUtc > now, ct),
            RegistersNeedingAttention: attention,
            RecentActivity: await AuditRowsAsync(db, null, 8, ct),
            LicencesNeedingAttention: await LicencesNeedingAttentionAsync(db, ct)));
    }

    internal static async Task<IResult> ListBusinessesAsync(string? search, HeadOfficeDbContext db, CancellationToken ct)
    {
        var onlineSince = DateTime.UtcNow - WebsiteEndpoints.OnlineWithin;
        var query = db.Tenants.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t => EF.Functions.Like(t.Name, $"%{search.Trim()}%"));   // any case, SQLite and SQL Server

        var rows = await query
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.CreatedUtc,
                Stores = db.Stores.Count(s => s.TenantId == t.Id),
                Registers = db.Registers.Count(r => r.TenantId == t.Id && !r.IsRevoked),
                Online = db.Registers.Count(r => r.TenantId == t.Id && !r.IsRevoked && r.LastSeenUtc >= onlineSince),
                LastSeen = db.Registers.Where(r => r.TenantId == t.Id && !r.IsRevoked).Max(r => r.LastSeenUtc)
            })
            .Take(500)
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var licences = await db.TenantLicences.AsNoTracking().Where(l => ids.Contains(l.TenantId)).ToDictionaryAsync(l => l.TenantId, ct);
        var now = DateTime.UtcNow;

        return Results.Ok(rows.Select(r => new BusinessRow(r.Id, r.Name, r.CreatedUtc, r.Stores, r.Registers, r.Online, Utc(r.LastSeen),
            licences.TryGetValue(r.Id, out var l) ? Summary(l, r.Name, now) : null)).ToList());
    }

    internal static async Task<IResult> GetBusinessAsync(Guid tenantId, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return Results.NotFound();

        var stores = await db.Stores.AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.StoreCode).ToListAsync(ct);
        var registers = await RegisterRowsAsync(db, r => r.TenantId == tenantId, ct);
        var keys = await db.WebsiteKeys.AsNoTracking()
            .Where(k => k.TenantId == tenantId)
            .OrderBy(k => k.IsRevoked).ThenBy(k => k.Name)
            .Select(k => new WebsiteKeyRow(k.Id, k.Name, k.ApiKeyPrefix, k.CreatedUtc, k.LastUsedUtc, k.IsRevoked))
            .ToListAsync(ct);

        return Results.Ok(new BusinessDetail(
            tenant.Id,
            tenant.Name,
            tenant.CreatedUtc,
            stores.Select(s => new StoreDetail(s.Id, s.StoreCode, s.Name, s.TimeZoneId, s.CreatedUtc,
                registers.Where(r => r.StoreId == s.Id).OrderBy(r => r.IsRevoked).ThenBy(r => r.RegisterCode).ToList())).ToList(),
            keys));
    }

    internal static async Task<IResult> RenameBusinessAsync(Guid tenantId, RenameBusinessRequest request, HeadOfficeDbContext db, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = new[] { "Enter the business name (up to 200 characters)." } });

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return Results.NotFound();

        var name = request.Name.Trim();
        if (name != tenant.Name)
        {
            Audit.Add(db, http, "business.renamed", tenant.Id, $"Renamed business {tenant.Name} to {name}");
            tenant.Name = name;
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    // ---- Licences ----

    internal static async Task<IResult> GetLicenceAsync(Guid tenantId, HeadOfficeDbContext db, LicenceService licences, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return Results.NotFound();

        var licence = await licences.GetOrStartTrialAsync(tenantId, ct);
        return Results.Ok(await DetailAsync(db, licence, tenant.Name, ct));
    }

    /// <summary>
    /// Sets the plan, limits, dates and status. Registers get the change with their next heartbeat (within a minute).
    /// Every change is in the audit log with what changed; suspending or cancelling needs a message for the shop.
    /// </summary>
    internal static async Task<IResult> UpdateLicenceAsync(Guid tenantId, UpdateLicenceRequest request, HeadOfficeDbContext db, LicenceService licences, HttpContext http, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return Results.NotFound();

        var errors = new Dictionary<string, string[]>();
        if (LicencePlans.Find(request.Plan) is null)
            errors["plan"] = new[] { "Choose a plan." };
        if (!Enum.IsDefined(request.Status))
            errors["status"] = new[] { "Choose a status." };
        if (request.MaxStores is < 1 or > 100)
            errors["maxStores"] = new[] { "Stores: 1 to 100." };
        if (request.MaxRegisters is < 1 or > 500)
            errors["maxRegisters"] = new[] { "Registers: 1 to 500." };
        if (request.ValidUntilUtc.Year is < 2020 or > 2100)
            errors["validUntilUtc"] = new[] { "Enter the date it's paid until." };
        var features = (request.Features ?? Array.Empty<string>()).Select(f => f.Trim().ToLowerInvariant()).Distinct().ToList();
        if (features.Any(f => !LicenceFeatures.All.Contains(f)))
            errors["features"] = new[] { "Unknown feature." };
        var message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
        if (message?.Length > 300)
            errors["message"] = new[] { "The message for the shop can be up to 300 characters." };
        if (request.Status is LicenceStatus.Suspended or LicenceStatus.Cancelled && message is null)
            errors["message"] = new[] { "Say why, for the shop. It's shown on their registers." };
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        var licence = await licences.GetOrStartTrialAsync(tenantId, ct);
        var validUntil = DateTime.SpecifyKind(request.ValidUntilUtc, DateTimeKind.Utc);

        var changes = new List<string>();
        void Note(string what, object? from, object? to)
        {
            if (!Equals(from, to)) changes.Add($"{what} {from ?? "none"} → {to ?? "none"}");
        }
        Note("plan", LicencePlans.NameOf(licence.Plan), LicencePlans.NameOf(request.Plan));
        Note("status", licence.Status, request.Status);
        Note("valid until", licence.ValidUntilUtc.ToString("yyyy-MM-dd"), validUntil.ToString("yyyy-MM-dd"));
        Note("stores", licence.MaxStores, request.MaxStores);
        Note("registers", licence.MaxRegisters, request.MaxRegisters);
        Note("features", Features(licence.FeatureList), Features(features));
        Note("message", licence.Message, message);
        if (changes.Count == 0)
            return Results.Ok(await DetailAsync(db, licence, tenant.Name, ct));

        licence.Plan = LicencePlans.Find(request.Plan)!.Code;
        licence.Status = request.Status;
        licence.ValidUntilUtc = validUntil;
        licence.MaxStores = request.MaxStores;
        licence.MaxRegisters = request.MaxRegisters;
        licence.Features = string.Join(',', features);
        licence.Message = message;
        licence.UpdatedUtc = DateTime.UtcNow;

        var note = string.IsNullOrWhiteSpace(request.Note) ? "" : $" ({request.Note.Trim()})";
        Audit.Add(db, http, "licence.changed", tenantId, $"Licence for {tenant.Name}: {string.Join("; ", changes)}{note}");
        await db.SaveChangesAsync(ct);

        return Results.Ok(await DetailAsync(db, licence, tenant.Name, ct));
    }

    private static string Features(IEnumerable<string> features)
    {
        var list = features.Select(LicenceFeatures.Label).ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }

    private static async Task<LicenceDetail> DetailAsync(HeadOfficeDbContext db, TenantLicence licence, string name, CancellationToken ct) =>
        new(licence.Plan, licence.Status, licence.ValidUntilUtc, licence.MaxStores, licence.MaxRegisters, licence.FeatureList, licence.Message,
            licence.UpdatedUtc,
            await db.Stores.CountAsync(s => s.TenantId == licence.TenantId, ct),
            await db.Registers.CountAsync(r => r.TenantId == licence.TenantId && !r.IsRevoked, ct),
            LicenceService.StateOf(licence, name, DateTime.UtcNow));

    private static LicenceSummary Summary(TenantLicence licence, string name, DateTime now)
    {
        var state = LicenceService.StateOf(licence, name, now);
        return new LicenceSummary(licence.Plan, licence.Status, licence.ValidUntilUtc, state.Level, state.Headline);
    }

    /// <summary>Suspended, ended, in grace or ending within 14 days: the businesses to chase.</summary>
    private static async Task<IReadOnlyList<BusinessRow>> LicencesNeedingAttentionAsync(HeadOfficeDbContext db, CancellationToken ct)
    {
        var soon = DateTime.UtcNow.AddDays(14);
        var rows = await db.TenantLicences.AsNoTracking()
            .Where(l => l.Status == LicenceStatus.Suspended || l.ValidUntilUtc <= soon)
            .Where(l => l.Status != LicenceStatus.Cancelled)
            .Join(db.Tenants, l => l.TenantId, t => t.Id, (l, t) => new { Licence = l, t.Name, t.CreatedUtc })
            .OrderBy(x => x.Licence.ValidUntilUtc)
            .Take(10)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        return rows.Select(x => new BusinessRow(x.Licence.TenantId, x.Name, x.CreatedUtc, 0, 0, 0, null, Summary(x.Licence, x.Name, now))).ToList();
    }

    /// <summary>Every register; ?status=offline for those that need attention.</summary>
    internal static async Task<IResult> ListRegistersAsync(string? status, HeadOfficeDbContext db, CancellationToken ct)
    {
        var rows = await RegisterRowsAsync(db, r => true, ct);
        IEnumerable<RegisterRow> result = status switch
        {
            "offline" => rows.Where(r => !r.IsRevoked && !r.Online),
            "online" => rows.Where(r => r.Online),
            "revoked" => rows.Where(r => r.IsRevoked),
            _ => rows
        };
        return Results.Ok(result.OrderBy(r => r.BusinessName).ThenBy(r => r.StoreCode).ThenBy(r => r.RegisterCode).ToList());
    }

    internal static async Task<IResult> ListAuditAsync(Guid? tenantId, int? take, HeadOfficeDbContext db, CancellationToken ct) =>
        Results.Ok(await AuditRowsAsync(db, tenantId, Math.Clamp(take ?? 100, 1, 500), ct));

    private static async Task<List<RegisterRow>> RegisterRowsAsync(HeadOfficeDbContext db, System.Linq.Expressions.Expression<Func<Register, bool>> filter, CancellationToken ct)
    {
        var onlineSince = DateTime.UtcNow - WebsiteEndpoints.OnlineWithin;
        var rows = await (from r in db.Registers.AsNoTracking().Where(filter)
                          join s in db.Stores.AsNoTracking() on r.StoreId equals s.Id
                          join t in db.Tenants.AsNoTracking() on r.TenantId equals t.Id
                          select new { r, s.StoreCode, BusinessName = t.Name })
            .ToListAsync(ct);

        return rows.Select(x => new RegisterRow(
                x.r.Id, x.r.TenantId, x.BusinessName, x.r.StoreId, x.StoreCode, x.r.RegisterCode, x.r.DeviceName, x.r.ApiKeyPrefix,
                x.r.ActivatedUtc, x.r.LastSeenUtc, !x.r.IsRevoked && x.r.LastSeenUtc >= onlineSince, x.r.IsRevoked))
            .ToList();
    }

    private static async Task<List<AuditEntryRow>> AuditRowsAsync(HeadOfficeDbContext db, Guid? tenantId, int take, CancellationToken ct)
    {
        var query = db.AuditEntries.AsNoTracking();
        if (tenantId is { } id)
            query = query.Where(a => a.TenantId == id);

        return await (from a in query.OrderByDescending(a => a.Id).Take(take)
                      join t in db.Tenants.AsNoTracking() on a.TenantId equals (Guid?)t.Id into business
                      from t in business.DefaultIfEmpty()
                      orderby a.Id descending
                      select new AuditEntryRow(a.Id, a.AtUtc, a.Actor, a.Action, a.TenantId, t == null ? null : t.Name, a.Summary))
            .ToListAsync(ct);
    }

    // Times from an aggregate (Max) skip the model's UTC marking; mark them here.
    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    // ---- Staff ----

    /// <summary>
    /// Checks email + password (+ the 6-digit code once set up). Five wrong tries lock the account for
    /// 15 minutes. The answer never says whether the email exists.
    /// </summary>
    internal static async Task<IResult> SignInAsync(StaffSignInRequest request, HeadOfficeDbContext db, HttpContext http, CancellationToken ct)
    {
        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        var user = await db.PlatformUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
        var now = DateTime.UtcNow;

        if (user is null || !user.IsActive)
        {
            // Same work as a real check, so the time taken doesn't reveal which emails exist.
            Hasher.VerifyHashedPassword(new PlatformUser(), DummyHash, request.Password ?? "");
            return Problem(SignInProblems.Invalid, "Email or password is wrong.");
        }

        if (user.LockedUntilUtc > now)
            return Problem(SignInProblems.Locked, $"Too many wrong tries. Try again after {user.LockedUntilUtc:HH:mm} UTC.");

        var passwordOk = Hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? "") != PasswordVerificationResult.Failed;
        if (!passwordOk)
            return await FailAsync(user, db, http, ct, "Email or password is wrong.");

        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.Code))
                return Problem(SignInProblems.CodeRequired, "Enter the 6-digit code from your authenticator app.");

            var step = Totp.Verify(user.TotpSecret!, request.Code, DateTimeOffset.UtcNow, user.LastTotpStep);
            if (step is null)
                return await FailAsync(user, db, http, ct, "That code is wrong or already used. Wait for the next one.");
            user.LastTotpStep = step.Value;
        }

        user.FailedSignIns = 0;
        user.LockedUntilUtc = null;
        user.LastSignInUtc = now;
        db.AuditEntries.Add(new AuditEntry { Actor = user.Email, Action = "staff.signed-in", Summary = $"{user.Name} signed in" });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new StaffSignInResponse(user.Id, user.Email, user.Name, user.Role, NeedsTwoFactorSetup: !user.TwoFactorEnabled));
    }

    private static async Task<IResult> FailAsync(PlatformUser user, HeadOfficeDbContext db, HttpContext http, CancellationToken ct, string message)
    {
        user.FailedSignIns++;
        if (user.FailedSignIns >= MaxFailedSignIns)
        {
            user.FailedSignIns = 0;
            user.LockedUntilUtc = DateTime.UtcNow + LockFor;
            db.AuditEntries.Add(new AuditEntry { Actor = user.Email, Action = "staff.locked", Summary = $"{user.Name} locked for 15 minutes after {MaxFailedSignIns} wrong sign-ins" });
        }
        await db.SaveChangesAsync(ct);
        return Problem(SignInProblems.Invalid, message);
    }

    private static IResult Problem(string type, string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status401Unauthorized, type: type, title: "Sign-in refused");

    // A hash of a random password, used only to spend the same time when the email is unknown.
    private static readonly string DummyHash = Hasher.HashPassword(new PlatformUser(), Guid.NewGuid().ToString());

    internal static async Task<IResult> ListStaffAsync(HeadOfficeDbContext db, CancellationToken ct) =>
        Results.Ok(await db.PlatformUsers.AsNoTracking()
            .OrderByDescending(u => u.IsActive).ThenBy(u => u.Name)
            .Select(u => new StaffRow(u.Id, u.Email, u.Name, u.Role, u.TwoFactorEnabled, u.IsActive, u.LastSignInUtc))
            .ToListAsync(ct));

    internal static async Task<IResult> CreateStaffAsync(CreateStaffRequest request, HeadOfficeDbContext db, HttpContext http, CancellationToken ct)
    {
        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        var errors = new Dictionary<string, string[]>();
        if (!email.Contains('@') || email.Length > 256) errors["email"] = new[] { "Enter a valid email address." };
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100) errors["name"] = new[] { "Enter their name." };
        if (!StaffRoles.IsValid(request.Role)) errors["role"] = new[] { "Choose Owner or Support." };
        if (PasswordProblem(request.Password) is { } problem) errors["password"] = new[] { problem };
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);
        if (await db.PlatformUsers.AnyAsync(u => u.Email == email, ct))
            return Results.Conflict("Someone with that email already has an account.");

        var user = new PlatformUser { Email = email, Name = request.Name.Trim(), Role = request.Role };
        user.PasswordHash = Hasher.HashPassword(user, request.Password);
        db.PlatformUsers.Add(user);
        Audit.Add(db, http, "staff.created", null, $"Added {user.Name} ({user.Email}) as {user.Role}");
        await db.SaveChangesAsync(ct);

        return Results.Ok(new StaffRow(user.Id, user.Email, user.Name, user.Role, false, true, null));
    }

    internal static async Task<IResult> DisableStaffAsync(Guid staffId, HeadOfficeDbContext db, HttpContext http, CancellationToken ct)
    {
        var user = await db.PlatformUsers.FirstOrDefaultAsync(u => u.Id == staffId, ct);
        if (user is null)
            return Results.NotFound();

        // Never leave the Portal without anyone who can manage it.
        if (user.Role == StaffRoles.Owner && !await db.PlatformUsers.AnyAsync(u => u.Id != staffId && u.IsActive && u.Role == StaffRoles.Owner, ct))
            return Results.Conflict("This is the last active owner. Add another owner first.");

        if (user.IsActive)
        {
            user.IsActive = false;
            Audit.Add(db, http, "staff.disabled", null, $"Disabled {user.Name} ({user.Email})");
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    /// <summary>A new secret for the authenticator app. Replaces any earlier unconfirmed one.</summary>
    internal static async Task<IResult> StartTwoFactorAsync(Guid staffId, HeadOfficeDbContext db, CancellationToken ct)
    {
        var user = await db.PlatformUsers.FirstOrDefaultAsync(u => u.Id == staffId && u.IsActive, ct);
        if (user is null)
            return Results.NotFound();
        if (user.TwoFactorEnabled)
            return Results.Conflict("Two-step sign-in is already set up.");

        user.PendingTotpSecret = Totp.NewSecret();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new TwoFactorSetup(user.PendingTotpSecret, Totp.OtpAuthUri(user.PendingTotpSecret, user.Email)));
    }

    /// <summary>The first code from the app proves it was set up right; only then is it switched on.</summary>
    internal static async Task<IResult> ConfirmTwoFactorAsync(Guid staffId, TwoFactorConfirmRequest request, HeadOfficeDbContext db, HttpContext http, CancellationToken ct)
    {
        var user = await db.PlatformUsers.FirstOrDefaultAsync(u => u.Id == staffId && u.IsActive, ct);
        if (user is null)
            return Results.NotFound();
        if (user.PendingTotpSecret is null)
            return Results.Conflict("Start the setup again.");

        var step = Totp.Verify(user.PendingTotpSecret, request.Code, DateTimeOffset.UtcNow);
        if (step is null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = new[] { "That code doesn't match. Check the app shows NepPOS Portal and try the next code." } });

        user.TotpSecret = user.PendingTotpSecret;
        user.PendingTotpSecret = null;
        user.TwoFactorEnabled = true;
        user.LastTotpStep = step.Value;
        Audit.Add(db, http, "staff.two-factor-on", null, $"{user.Name} set up two-step sign-in");
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    internal static async Task<IResult> ChangePasswordAsync(Guid staffId, ChangePasswordRequest request, HeadOfficeDbContext db, HttpContext http, CancellationToken ct)
    {
        var user = await db.PlatformUsers.FirstOrDefaultAsync(u => u.Id == staffId && u.IsActive, ct);
        if (user is null)
            return Results.NotFound();
        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = new[] { "Your current password is wrong." } });
        if (PasswordProblem(request.NewPassword) is { } problem)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = new[] { problem } });

        user.PasswordHash = Hasher.HashPassword(user, request.NewPassword);
        Audit.Add(db, http, "staff.password-changed", null, $"{user.Name} changed their password");
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Long beats complicated: 12 characters or more, not just one repeated character.</summary>
    internal static string? PasswordProblem(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < 12 ? "Use at least 12 characters (a short sentence works well)."
        : password.Distinct().Count() < 4 ? "That password is too easy to guess."
        : null;

    /// <summary>
    /// First start with no staff at all: creates the owner from HeadOffice:Owner:Email / :Name / :Password
    /// (user secrets or App Service settings). They are asked to set up two-step sign-in on first sign-in.
    /// </summary>
    public static void CreateFirstOwner(HeadOfficeDbContext db, IConfiguration configuration, ILogger logger)
    {
        if (db.PlatformUsers.Any())
            return;

        var email = configuration["HeadOffice:Owner:Email"]?.Trim().ToLowerInvariant();
        var password = configuration["HeadOffice:Owner:Password"];
        if (string.IsNullOrEmpty(email) || PasswordProblem(password) is { } problem)
        {
            logger.LogWarning("No Portal staff yet. Set HeadOffice:Owner:Email and HeadOffice:Owner:Password (12+ characters) to create the first owner.");
            return;
        }

        var owner = new PlatformUser { Email = email, Name = configuration["HeadOffice:Owner:Name"]?.Trim() is { Length: > 0 } n ? n : "Owner", Role = StaffRoles.Owner };
        owner.PasswordHash = Hasher.HashPassword(owner, password!);
        db.PlatformUsers.Add(owner);
        db.AuditEntries.Add(new AuditEntry { Actor = "setup", Action = "staff.created", Summary = $"Created the first owner {owner.Email}" });
        db.SaveChanges();
        logger.LogInformation("Created the first Portal owner {Email}.", owner.Email);
    }
}
