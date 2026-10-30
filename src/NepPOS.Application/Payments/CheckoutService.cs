using NepPOS.Application.Abstractions;
using NepPOS.Domain.Entities;

namespace NepPOS.Application.Payments;

public interface ICheckoutService
{
    /// <summary>
    /// Records the payments, assigns the invoice number and saves a fully-paid sale.
    /// Safe to call again after a failure: it never talks to the EFTPOS terminal,
    /// so retrying a failed save can never charge the customer twice.
    /// </summary>
    /// <returns>The invoice number.</returns>
    Task<long> CompleteSaleAsync(SaleTransaction sale, TenderSession tender, CancellationToken cancellationToken = default);
}

public sealed class CheckoutService : ICheckoutService
{
    private readonly ISaleTransactionRepository _repository;

    public CheckoutService(ISaleTransactionRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<long> CompleteSaleAsync(SaleTransaction sale, TenderSession tender, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(tender);

        if (!tender.IsComplete)
            throw new InvalidOperationException("The sale is not fully paid yet.");
        if (tender.TransactionId != sale.Id)
            throw new InvalidOperationException("These payments belong to a different sale.");

        // On a retry these steps were already done — skip them and just try the save again.
        if (sale.Status == SaleTransactionStatus.InProgress)
        {
            if (sale.Payments.Count == 0)
                sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);

            var invoiceNumber = await _repository.GetNextInvoiceNumberAsync(sale.StoreId, cancellationToken);
            sale.Complete(invoiceNumber);
        }

        await _repository.AddAsync(sale, cancellationToken);
        return sale.InvoiceNumber!.Value;
    }
}
