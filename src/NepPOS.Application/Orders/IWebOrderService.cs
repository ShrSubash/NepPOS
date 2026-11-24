using NepPOS.Application.Payments;
using NepPOS.Domain.Entities;

namespace NepPOS.Application.Orders;

/// <summary>
/// A website order rung up as a sale: the sale (picked lines only), what was prepaid online and goes on account, and
/// what's left to pay at the store. <see cref="RefundDue"/> is prepaid money for items the store didn't have.
/// </summary>
public sealed record WebOrderSale(SaleTransaction Sale, decimal PaidOnline, decimal OnAccount, Customer? Customer, decimal DueAtStore, decimal RefundDue)
{
    /// <summary>The tender name for money already taken on the website.</summary>
    public const string PaidOnlineTender = "Paid online";

    /// <summary>Adds the online and account payments to <paramref name="tender"/>; the cashier takes the rest.</summary>
    public void Prepay(TenderSession tender, string reference)
    {
        ArgumentNullException.ThrowIfNull(tender);
        if (PaidOnline > 0)
            tender.AddOther(PaidOnlineTender, Math.Min(PaidOnline, tender.Remaining), reference);
        if (OnAccount > 0 && Customer is not null && tender.Remaining > 0)
            tender.AddAccount(Customer.Code, Customer.Name, Math.Min(OnAccount, tender.Remaining));
    }
}

/// <summary>Website orders for this store: pick them, have them ready, and ring them up when collected or sent.</summary>
public interface IWebOrderService
{
    /// <summary>Open orders (oldest first), or the latest finished ones too when <paramref name="includeClosed"/>.</summary>
    Task<IReadOnlyList<WebOrder>> ListAsync(bool includeClosed = false, CancellationToken cancellationToken = default);

    Task<WebOrder?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Orders not yet ready: the number on the register's Web orders button.</summary>
    Task<int> WaitingCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Someone starts picking it. The website shows "Being picked".</summary>
    Task StartPickingAsync(Guid id, string? employeeName, CancellationToken cancellationToken = default);

    /// <summary>Saves how many of each line (by line id) were found; lines not given are left as they are.</summary>
    Task SavePickedAsync(Guid id, IReadOnlyDictionary<int, int> picked, CancellationToken cancellationToken = default);

    /// <summary>Picked and packed. Lines never picked count as all found.</summary>
    Task MarkReadyAsync(Guid id, string? employeeName, CancellationToken cancellationToken = default);

    /// <summary>Builds the sale for what was picked. Nothing is saved until <see cref="CompleteAsync"/>.</summary>
    Task<WebOrderSale> PrepareSaleAsync(Guid id, int? employeeId, string? employeeName, CancellationToken cancellationToken = default);

    /// <summary>Saves the paid sale (stock comes off), marks the order completed and tells the website. Returns the invoice number.</summary>
    Task<long> CompleteAsync(Guid id, WebOrderSale prepared, IPaymentSession tender, CancellationToken cancellationToken = default);

    /// <summary>Cancels an order that isn't completed. Prepaid money is for the website to refund.</summary>
    Task CancelAsync(Guid id, string reason, string? employeeName, CancellationToken cancellationToken = default);
}
