using NepPOS.Domain.Entities;

namespace NepPOS.Application.Sales;

/// <param name="AlreadyReturned">Items of this line brought back on earlier returns.</param>
public sealed record ReturnableLine(SaleLineItem Line, int AlreadyReturned)
{
    public int Returnable => Math.Max(0, Line.Quantity - AlreadyReturned);
}

/// <summary>A sale found for a return, with what can still come back.</summary>
public sealed record ReturnableSale(SaleTransaction Sale, IReadOnlyList<ReturnableLine> Lines)
{
    public int DaysAgo(DateTime nowUtc) => (int)Math.Floor((nowUtc - Sale.CreatedAtUtc.UtcDateTime).TotalDays);
}

/// <summary>Finds a sale by its receipt to return items from it.</summary>
public interface IReturnsService
{
    /// <summary>By the receipt's barcode ("RT001-19") or just the invoice number on this register. Null when not found.</summary>
    Task<ReturnableSale?> FindAsync(string receiptCodeOrInvoice, CancellationToken cancellationToken = default);

    /// <summary>Sales on a day, newest first (no receipt: find it by day and amount).</summary>
    Task<IReadOnlyList<SaleTransaction>> SalesOnAsync(DateOnly day, CancellationToken cancellationToken = default);
}
