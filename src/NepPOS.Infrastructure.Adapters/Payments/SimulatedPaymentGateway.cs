using System.Globalization;
using NepPOS.Application.Payments;

namespace NepPOS.Infrastructure.Adapters.Payments;

/// <summary>
/// Dev-only fake terminal so the whole checkout can be tested before the Ingenico adapter exists.
/// Registered in Debug builds only. Approves everything except amounts ending in .13 cents,
/// which decline — use that to test the decline path.
/// </summary>
public sealed class SimulatedPaymentGateway : IPaymentGateway
{
    public string Name => "Simulated EFTPOS (dev only)";

    public async Task<PaymentResult> PurchaseAsync(PaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Mimic the customer tapping their card.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        var cents = decimal.Truncate(request.Amount * 100m) % 100m;
        if (cents == 13m)
            return new PaymentResult(false, "51", "DECLINED - INSUFFICIENT FUNDS");

        return new PaymentResult(
            Approved: true,
            ResponseCode: "00",
            ResponseText: "APPROVED",
            AuthCode: Random.Shared.Next(100000, 999999).ToString(CultureInfo.InvariantCulture),
            CardType: "VISA",
            MaskedPan: "**** **** **** 4242");
    }
}
