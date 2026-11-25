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
    /// A decline is a normal result (Approved = false), never an exception.
    /// Throw <see cref="PaymentOutcomeUnknownException"/> when the request reached the
    /// terminal but its result could not be confirmed — the card MAY have been charged.
    /// </summary>
    Task<PaymentResult> PurchaseAsync(PaymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refunds money to a card on the terminal (the customer taps their card). Same rules as a purchase: a decline is a
    /// normal result, and <see cref="PaymentOutcomeUnknownException"/> means check the terminal before trying again.
    /// </summary>
    Task<PaymentResult> RefundAsync(PaymentRequest request, CancellationToken cancellationToken = default);
}

/// <param name="TransactionId">The sale's GUID, sent as the merchant reference so terminal logs tie back to the sale.</param>
public sealed record PaymentRequest(Guid TransactionId, decimal Amount);

/// <param name="CustomerReceipt">The customer's card slip text from the terminal (always kept, even if the terminal printed it).</param>
/// <param name="MerchantReceipt">The store's card slip text from the terminal (always kept).</param>
/// <param name="CustomerReceiptPrinted">True when the terminal already printed the customer slip itself, so the POS must not print it again.</param>
/// <param name="MerchantReceiptPrinted">True when the terminal already printed the merchant slip itself.</param>
public sealed record PaymentResult(
    bool Approved,
    string ResponseCode,
    string ResponseText,
    string? AuthCode = null,
    string? CardType = null,
    string? MaskedPan = null,
    string? CustomerReceipt = null,
    string? MerchantReceipt = null,
    bool CustomerReceiptPrinted = false,
    bool MerchantReceiptPrinted = false);

/// <summary>
/// The purchase was sent but the result could not be confirmed (connection lost mid-transaction
/// and the status lookup also failed). The cashier must check the terminal before charging again.
/// </summary>
public sealed class PaymentOutcomeUnknownException : Exception
{
    public PaymentOutcomeUnknownException(string terminalReference, Exception? innerException = null)
        : base($"EFTPOS result could not be confirmed (reference {terminalReference}).", innerException)
    {
        TerminalReference = terminalReference;
    }

    /// <summary>The reference the terminal knows this attempt by (Spice posRefId), for looking it up.</summary>
    public string TerminalReference { get; }
}
