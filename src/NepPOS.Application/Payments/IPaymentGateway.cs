namespace NepPOS.Application.Payments;

/// <summary>
/// Vendor-agnostic EFTPOS terminal contract. Ingenico, Tyro, WorldLine… each become one
/// adapter class in Infrastructure.Adapters plus one DI line — no changes to core logic.
/// </summary>
public interface IPaymentGateway
{
    string Name { get; }

    /// <summary>
    /// Sends a purchase to the terminal and waits for the customer to finish.
    /// A decline is a normal result (Approved = false), never an exception;
    /// throw only for terminal/connection faults.
    /// </summary>
    Task<PaymentResult> PurchaseAsync(PaymentRequest request, CancellationToken cancellationToken = default);
}

/// <param name="TransactionId">The sale's GUID, sent as the merchant reference so terminal logs tie back to the sale.</param>
public sealed record PaymentRequest(Guid TransactionId, decimal Amount);

public sealed record PaymentResult(
    bool Approved,
    string ResponseCode,
    string ResponseText,
    string? AuthCode = null,
    string? CardType = null,
    string? MaskedPan = null);
