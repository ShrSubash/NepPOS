using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NepPOS.Contracts.Catalog;
using NepPOS.Contracts.Reports;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Catalog;
using NepPOS.HeadOffice.Api.Data;
using NepPOS.HeadOffice.Api.Licensing;
using NepPOS.HeadOffice.Api.Reports;
using NepPOS.HeadOffice.Api.Security;

namespace NepPOS.HeadOffice.Api.Endpoints;

/// <summary>
/// What a business's website reads: its stores (online or not) and their sales. A website key only
/// ever sees its own business; another business's store id answers 404, as if it didn't exist.
/// </summary>
public static class WebsiteEndpoints
{
    /// <summary>A register that hasn't sent a heartbeat for this long counts as offline.</summary>
    public static readonly TimeSpan OnlineWithin = TimeSpan.FromMinutes(10);

    private const int MaxDays = 400;

    public static void MapWebsiteEndpoints(this IEndpointRouteBuilder app)
    {
        var website = app.MapGroup("/api/v1").RequireAuthorization(WebsiteApiKeyHandler.Policy)
            .AddEndpointFilter<WebsiteLicenceFilter>().WithTags("Website");
        website.MapGet("/stores", GetStoresAsync);
        website.MapGet("/reports/sales", GetSalesAsync);
        website.MapGet("/catalog/changes", GetCatalogChangesAsync);
        website.MapWebsiteAccountEndpoints();
        website.MapGet("/stock/changes", GetStockChangesAsync);
    }

    internal static async Task<IResult> GetStoresAsync(HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        var onlineSince = DateTime.UtcNow - OnlineWithin;

        var stores = await db.Stores.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.StoreCode)
            .Select(s => new
            {
                s.Id,
                s.StoreCode,
                s.Name,
                LastSeen = db.Registers.Where(r => r.StoreId == s.Id && !r.IsRevoked).Max(r => r.LastSeenUtc),
                LastSale = db.ReceivedEvents.Where(e => e.StoreId == s.Id && e.Type == SyncEventTypes.SaleCompleted).Max(e => (DateTime?)e.OccurredAtUtc)
            })
            .ToListAsync(ct);

        return Results.Ok(stores
            .Select(s => new StoreSummary(s.Id, s.StoreCode, s.Name, s.LastSeen >= onlineSince, Utc(s.LastSeen), Utc(s.LastSale)))
            .ToList());
    }

    // Times from an aggregate (Max) skip the model's UTC marking; mark them here.
    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    internal static async Task<IResult> GetSalesAsync(Guid storeId, DateOnly from, DateOnly to, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        if (to < from)
            (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber > MaxDays)
            return Results.Problem($"Ask for at most {MaxDays} days at a time.", statusCode: 400);

        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId && s.TenantId == tenantId, ct);
        if (store is null)
            return Results.NotFound();

        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(store.TimeZoneId); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.Utc; }

        // A day either side, then the exact trading-day filter in the builder.
        var (fromUtc, toUtc) = SalesReportBuilder.RoughUtcWindow(from, to);
        var events = await db.ReceivedEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.StoreId == store.Id && e.Type == SyncEventTypes.SaleCompleted)
            .Where(e => e.OccurredAtUtc >= fromUtc && e.OccurredAtUtc < toUtc)
            .Select(e => new { e.OccurredAtUtc, e.Version, e.Payload })
            .ToListAsync(ct);

        var sales = events
            .Where(e => e.Version == SaleCompletedV1.Version)
            .Select(e => (e.OccurredAtUtc, Sale: JsonSerializer.Deserialize<SaleCompletedV1>(e.Payload, SyncJson.Options)!))
            .ToList();

        return Results.Ok(SalesReportBuilder.Build(store.Id, store.StoreCode, store.Name, from, to, sales, zone));
    }

    /// <summary>
    /// A store's products changed after <paramref name="after"/> (a change number), oldest first.
    /// <c>after=0</c> gives the whole catalog. Inactive products are included, so the website can stop selling them.
    /// </summary>
    /// <summary>A store's stock levels changed after change number <paramref name="after"/>, oldest first (0 = all).</summary>
    internal static async Task<IResult> GetStockChangesAsync(Guid storeId, long? after, int? limit, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        if (!await db.Stores.AnyAsync(s => s.Id == storeId && s.TenantId == tenantId, ct))
            return Results.NotFound();

        var take = Math.Clamp(limit ?? 500, 1, 1000);
        var from = Math.Max(0, after ?? 0);
        var rows = await db.StockLevels.AsNoTracking()
            .Where(s => s.StoreId == storeId && s.LastEventId > from)
            .OrderBy(s => s.LastEventId)
            .Take(take + 1)
            .ToListAsync(ct);
        var more = rows.Count > take;
        var page = rows.Take(take).ToList();
        return Results.Ok(new StockChanges(
            page.Select(s => new StockLevel(s.ProductId, s.StockOnHand, DateTime.SpecifyKind(s.UpdatedUtc, DateTimeKind.Utc), s.LastEventId)).ToList(),
            page.Count > 0 ? page[^1].LastEventId : from,
            more));
    }

    internal static async Task<IResult> GetCatalogChangesAsync(Guid storeId, long? after, int? limit, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        if (!await db.Stores.AsNoTracking().AnyAsync(s => s.Id == storeId && s.TenantId == tenantId, ct))
            return Results.NotFound();

        var from = Math.Max(0, after ?? 0);
        var take = Math.Clamp(limit ?? CatalogChanges.DefaultLimit, 1, CatalogChanges.MaxLimit);

        // One extra row tells us whether there is more without a second query.
        var rows = await db.CatalogItems.AsNoTracking()
            .Where(c => c.StoreId == storeId && c.TenantId == tenantId && c.LastEventId > from)
            .OrderBy(c => c.LastEventId)
            .Take(take + 1)
            .ToListAsync(ct);

        var more = rows.Count > take;
        var page = rows.Take(take).Select(CatalogProjection.ToContract).ToList();
        return Results.Ok(new CatalogChanges(page, page.Count > 0 ? page[^1].ChangeNumber : from, more));
    }
}
