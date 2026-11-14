using Microsoft.EntityFrameworkCore;
using NepPOS.HeadOffice.Api.Data;
using NepPOS.HeadOffice.Api.Security;

namespace NepPOS.HeadOffice.Api.Endpoints;

/// <summary>
/// Setting up a customer: create the tenant, its stores, and a one-time activation code per register.
/// Protected by the platform admin key (X-Admin-Key) until the platform portal exists.
/// </summary>
public static class AdminEndpoints
{
    public sealed record CreateTenantRequest(string Name);
    public sealed record TenantResponse(Guid Id, string Name);
    public sealed record CreateStoreRequest(string StoreCode, string Name, string? TimeZoneId = null);
    public sealed record StoreResponse(Guid Id, Guid TenantId, string StoreCode, string Name, string TimeZoneId);
    public sealed record CreateWebsiteKeyRequest(string? Name);
    public sealed record WebsiteKeyResponse(Guid Id, Guid TenantId, string Name, string ApiKey);
    public sealed record CreateActivationCodeRequest(int? ValidDays);
    public sealed record ActivationCodeResponse(string Code, Guid StoreId, string StoreCode, DateTime ExpiresUtc);
    public sealed record RegisterResponse(Guid Id, string RegisterCode, string? DeviceName, string ApiKeyPrefix, DateTime ActivatedUtc, DateTime? LastSeenUtc, bool IsRevoked);

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").AddEndpointFilter<AdminKeyFilter>().WithTags("Admin");

        admin.MapPost("/tenants", async (CreateTenantRequest request, HeadOfficeDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = new[] { "Enter the business name." } });

            var tenant = new Tenant { Name = request.Name.Trim() };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/admin/tenants/{tenant.Id}", new TenantResponse(tenant.Id, tenant.Name));
        });

        admin.MapPost("/tenants/{tenantId:guid}/stores", async (Guid tenantId, CreateStoreRequest request, HeadOfficeDbContext db, CancellationToken ct) =>
        {
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct))
                return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.StoreCode) || string.IsNullOrWhiteSpace(request.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["storeCode"] = new[] { "Enter a store code (e.g. STORE-001) and a name." } });

            var code = request.StoreCode.Trim().ToUpperInvariant();
            if (await db.Stores.AnyAsync(s => s.TenantId == tenantId && s.StoreCode == code, ct))
                return Results.Conflict($"This business already has a store {code}.");

            var timeZone = string.IsNullOrWhiteSpace(request.TimeZoneId) ? "Australia/Sydney" : request.TimeZoneId.Trim();
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["timeZoneId"] = new[] { "Use a time zone like Australia/Sydney or Australia/Perth." } });

            var store = new Store { TenantId = tenantId, StoreCode = code, Name = request.Name.Trim(), TimeZoneId = timeZone };
            db.Stores.Add(store);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/admin/stores/{store.Id}", new StoreResponse(store.Id, store.TenantId, store.StoreCode, store.Name, store.TimeZoneId));
        });

        // The code is returned once, in plain text, to read out or send to the store; only its hash is kept.
        admin.MapPost("/stores/{storeId:guid}/activation-codes", async (Guid storeId, CreateActivationCodeRequest? request, HeadOfficeDbContext db, CancellationToken ct) =>
        {
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct);
            if (store is null)
                return Results.NotFound();

            var code = Secrets.NewActivationCode();
            var activation = new ActivationCode
            {
                TenantId = store.TenantId,
                StoreId = store.Id,
                CodeHash = Secrets.Hash(Secrets.NormaliseCode(code)),
                ExpiresUtc = DateTime.UtcNow.AddDays(Math.Clamp(request?.ValidDays ?? 7, 1, 30))
            };
            db.ActivationCodes.Add(activation);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new ActivationCodeResponse(code, store.Id, store.StoreCode, activation.ExpiresUtc));
        });

        admin.MapGet("/stores/{storeId:guid}/registers", async (Guid storeId, HeadOfficeDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Registers.AsNoTracking()
                .Where(r => r.StoreId == storeId)
                .OrderBy(r => r.RegisterCode)
                .Select(r => new RegisterResponse(r.Id, r.RegisterCode, r.DeviceName, r.ApiKeyPrefix, r.ActivatedUtc, r.LastSeenUtc, r.IsRevoked))
                .ToListAsync(ct)));

        // The key the business's website uses to read its reports. Shown once; only its hash is kept.
        admin.MapPost("/tenants/{tenantId:guid}/website-keys", async (Guid tenantId, CreateWebsiteKeyRequest? request, HeadOfficeDbContext db, CancellationToken ct) =>
        {
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct))
                return Results.NotFound();

            var apiKey = Secrets.NewWebsiteKey();
            var key = new WebsiteKey
            {
                TenantId = tenantId,
                Name = string.IsNullOrWhiteSpace(request?.Name) ? "Website" : request!.Name!.Trim()[..Math.Min(100, request.Name.Trim().Length)],
                ApiKeyHash = Secrets.Hash(apiKey),
                ApiKeyPrefix = apiKey[..10]
            };
            db.WebsiteKeys.Add(key);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new WebsiteKeyResponse(key.Id, tenantId, key.Name, apiKey));
        });

        admin.MapPost("/website-keys/{keyId:guid}/revoke", async (Guid keyId, HeadOfficeDbContext db, CancellationToken ct) =>
        {
            var key = await db.WebsiteKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
            if (key is null)
                return Results.NotFound();

            key.IsRevoked = true;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // A stolen or replaced PC: its key stops working on the next request.
        admin.MapPost("/registers/{registerId:guid}/revoke", async (Guid registerId, HeadOfficeDbContext db, CancellationToken ct) =>
        {
            var register = await db.Registers.FirstOrDefaultAsync(r => r.Id == registerId, ct);
            if (register is null)
                return Results.NotFound();

            register.IsRevoked = true;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
