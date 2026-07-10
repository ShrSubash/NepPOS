using NepPOS.Application.Abstractions;
using NepPOS.Domain.Entities;

namespace NepPOS.Application;

/// <summary>
/// Default implementation of IOrderProcessor.
/// Maps an ExternalOrder (from any platform adapter) into a SaleTransaction,
/// following the same completion path as an in-store sale.
/// </summary>
public sealed class OrderProcessor : IOrderProcessor
{
    private readonly ISaleTransactionRepository _repository;

    // GST rate is a placeholder here — this belongs in the Config layer once it exists.
    // Flagged deliberately rather than hardcoded silently, so it isn't missed later.
    private const decimal DefaultGstRate = 0.10m;
    private const string SystemRegisterId = "EXTERNAL-ORDER-INTAKE";

    public OrderProcessor(ISaleTransactionRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<SaleTransaction> ProcessAsync(ExternalOrder externalOrder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(externalOrder);

        var saleTransaction = new SaleTransaction(externalOrder.StoreId, SystemRegisterId);

        foreach (var line in externalOrder.Lines)
        {
            var lineItem = new SaleLineItem(
                sku: line.Sku,
                productName: line.Sku, // TODO: resolve real product name via a product lookup once Stock domain exists
                quantity: line.Quantity,
                unitPriceIncGst: line.UnitPriceIncGst,
                gstRate: DefaultGstRate);

            saleTransaction.AddLineItem(lineItem);
        }

        var invoiceNumber = await _repository.GetNextInvoiceNumberAsync(externalOrder.StoreId, cancellationToken);
        saleTransaction.Complete(invoiceNumber);

        await _repository.AddAsync(saleTransaction, cancellationToken);

        return saleTransaction;
    }
}