using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NepPOS.Contracts.Orders;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Data;

namespace NepPOS.HeadOffice.Api.Orders;

/// <summary>Keeps Head Office's copy of each website order up to date from <c>order.status</c> events.</summary>
public static class WebOrderProjection
{
    /// <summary>Reads an <c>order.status</c> payload. Returns why it's refused, or null.</summary>
    public static string? Read(SyncEvent e, out WebOrderStatusV1? status)
    {
        status = null;
        if (e.Type != SyncEventTypes.OrderStatus)
            return null;
        try { status = e.Payload.Deserialize<WebOrderStatusV1>(SyncJson.Options); }
        catch (JsonException ex) { return $"Order status payload can't be read: {ex.Message}"; }
        if (status is null || status.OrderId == Guid.Empty)
            return "An order status needs an orderId.";
        if (!WebOrderStatuses.All.Contains(status.Status))
            return $"Unknown order status \"{status.Status}\".";
        return null;
    }

    /// <summary>
    /// Applies a status to the order (if Head Office has it) and numbers the change for the website. A finished order
    /// (completed or cancelled) never changes again, so a late or repeated event can't undo it.
    /// </summary>
    public static async Task<bool> ApplyAsync(HeadOfficeDbContext db, Guid tenantId, WebOrderStatusV1 status, CancellationToken ct)
    {
        var order = db.WebOrders.Local.FirstOrDefault(o => o.OrderId == status.OrderId)
                    ?? await db.WebOrders.FirstOrDefaultAsync(o => o.OrderId == status.OrderId && o.TenantId == tenantId, ct);
        if (order is null || order.TenantId != tenantId || IsFinished(order.Status) || order.Status == status.Status && status.Status != WebOrderStatuses.Ready)
            return false;

        order.Status = status.Status;
        order.UpdatedUtc = DateTime.UtcNow;
        if (status.Picked is not null)
            order.PickedJson = JsonSerializer.Serialize(status.Picked, SyncJson.Options);
        if (status.FinalTotal is not null)
            order.FinalTotal = status.FinalTotal;
        if (status.RefundDue is not null)
            order.RefundDue = status.RefundDue;
        if (status.InvoiceNumber is not null)
            order.InvoiceNumber = status.InvoiceNumber;
        if (!string.IsNullOrWhiteSpace(status.Note))
            order.Note = status.Note.Length > 500 ? status.Note[..500] : status.Note;
        db.WebOrderChanges.Add(new WebOrderChange { TenantId = tenantId, OrderId = order.OrderId });
        return true;
    }

    public static bool IsFinished(string status) => status is WebOrderStatuses.Completed or WebOrderStatuses.Cancelled;

    public static WebOrderView View(WebOrderRow o, long changeNumber) => new(
        o.OrderId, o.StoreId, o.Reference, o.Status, o.Total, o.FinalTotal, o.RefundDue, o.InvoiceNumber, o.Note,
        o.PickedJson is null ? null : JsonSerializer.Deserialize<List<WebOrderPickedV1>>(o.PickedJson, SyncJson.Options),
        DateTime.SpecifyKind(o.UpdatedUtc, DateTimeKind.Utc), changeNumber);
}
