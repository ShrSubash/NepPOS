using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NepPOS.Contracts.Catalog;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Data;
using NepPOS.HeadOffice.Api.Security;

namespace NepPOS.HeadOffice.Api.Catalog;

/// <summary>
/// Keeps <see cref="CatalogItem"/> (one row per product per store) up to date from <c>product.changed</c>
/// events. It runs inside the same SaveChanges as storing the events, so the catalog never misses one.
/// </summary>
public static class CatalogProjection
{
    /// <summary>Reads and checks a <c>product.changed</c> payload. Returns why it's refused, or null.</summary>
    public static string? Read(SyncEvent e, out ProductChangedV1? product)
    {
        product = null;
        if (e.Version != ProductChangedV1.Version)
            return $"product.changed version {e.Version} isn't supported; this Head Office reads version {ProductChangedV1.Version}.";
        if (e.Payload.ValueKind != JsonValueKind.Object)
            return "Payload must be a JSON object.";

        ProductChangedV1? p;
        try { p = e.Payload.Deserialize<ProductChangedV1>(SyncJson.Options); }
        catch (JsonException ex) { return $"Product payload can't be read: {ex.Message}"; }

        var problem = p switch
        {
            null => "Product payload is empty.",
            _ when p.ProductId == Guid.Empty => "Missing productId.",
            _ when string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 200 => "Product name is missing or longer than 200.",
            _ when (p.Category ?? "").Length > 100 => "Category is longer than 100.",
            _ when p.Price < 0 => "Price can't be negative.",
            _ when (p.Barcode ?? "").Length > 50 || (p.Plu ?? "").Length > 50 => "Barcode or PLU is longer than 50.",
            _ => null
        };
        if (problem is null)
            product = p;
        return problem;
    }

    /// <summary>
    /// Applies the batch's product changes, oldest first. A change older than what is already held
    /// (it arrived late, or from a register whose clock is behind) is kept as an event but doesn't
    /// overwrite the newer product.
    /// </summary>
    public static async Task ApplyAsync(HeadOfficeDbContext db, RegisterIdentity caller,
        IReadOnlyList<(ReceivedEvent Row, ProductChangedV1 Product)> changes, CancellationToken ct)
    {
        if (changes.Count == 0)
            return;

        var ids = changes.Select(c => c.Product.ProductId).Distinct().ToList();
        var items = await db.CatalogItems
            .Where(c => c.StoreId == caller.StoreId && ids.Contains(c.ProductId))
            .ToDictionaryAsync(c => c.ProductId, ct);

        foreach (var (row, p) in changes.OrderBy(c => c.Row.Sequence))
        {
            if (items.TryGetValue(p.ProductId, out var item))
            {
                if (row.OccurredAtUtc < item.ChangedAtUtc)
                    continue;
            }
            else
            {
                item = new CatalogItem { TenantId = caller.TenantId, StoreId = caller.StoreId, ProductId = p.ProductId };
                db.CatalogItems.Add(item);
                items[p.ProductId] = item;
            }

            item.Name = p.Name.Trim();
            item.Category = (p.Category ?? "").Trim();
            item.Price = p.Price;
            item.Barcode = p.Barcode;
            item.Plu = p.Plu;
            item.EnableOnWebsite = p.EnableOnWebsite;
            item.IsActive = p.IsActive;
            item.VariantsJson = JsonSerializer.Serialize(p.Variants ?? Array.Empty<ProductVariantV1>(), SyncJson.Options);
            item.ChangedAtUtc = row.OccurredAtUtc;
            item.LastEvent = row;      // its id (the change number) is set when the batch is saved
        }
    }

    /// <summary>The website's view of one catalog row.</summary>
    public static CatalogProduct ToContract(CatalogItem item) => new(
        item.ProductId,
        item.Name,
        item.Category,
        item.Price,
        item.Barcode,
        item.Plu,
        item.EnableOnWebsite,
        item.IsActive,
        JsonSerializer.Deserialize<List<ProductVariantV1>>(item.VariantsJson, SyncJson.Options) ?? new List<ProductVariantV1>(),
        DateTime.SpecifyKind(item.ChangedAtUtc, DateTimeKind.Utc),
        item.LastEventId);
}
