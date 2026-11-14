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
    public sealed record CreateStoreRequest(string StoreCode, string Name);
    public sealed record StoreResponse(Guid Id, Guid TenantId, string StoreCode, string Name);
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

            var store = new Store { TenantId = tenantId, StoreCode = code, Name = request.Name.Trim() };
            db.Stores.Add(store);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/admin/stores/{store.Id}", new StoreResponse(store.Id, store.TenantId, store.StoreCode, store.Name));
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
