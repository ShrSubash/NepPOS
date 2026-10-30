using NepPOS.Application.Payments;

namespace NepPOS.Infrastructure.Adapters.Payments;

/// <summary>
/// Used in Release builds until a real terminal adapter (Ingenico) is registered.
/// Every card attempt is declined with a clear message, so cash sales keep working
/// and nobody thinks a card was charged.
/// </summary>
public sealed class NotConfiguredPaymentGateway : IPaymentGateway
{
    public string Name => "No EFTPOS terminal configured";

    public Task<PaymentResult> PurchaseAsync(PaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new PaymentResult(false, "NC", "EFTPOS TERMINAL NOT CONFIGURED"));
    }
}
