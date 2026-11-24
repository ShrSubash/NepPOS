using NepPOS.Domain.Entities;

namespace NepPOS.Application.Sales;

public sealed record GiftCardBalance(string Code, GiftCardKind Kind, decimal Balance, bool IsActive, DateTime IssuedUtc)
{
    public string KindName => Kind == GiftCardKind.StoreCredit ? "Store credit" : "Gift card";
}

/// <summary>Gift cards and store credit on this register. Balances change only when the sale using them is saved.</summary>
public interface IGiftCardService
{
    /// <summary>The card's balance, or null for an unknown code.</summary>
    Task<GiftCardBalance?> GetAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>A code not used yet: "GC-" or "SC-" and ten random digits (unique across all stores).</summary>
    Task<string> NewCodeAsync(GiftCardKind kind, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GiftCardBalance>> ListAsync(string? search = null, int take = 200, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GiftCardMovement>> HistoryAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Stops a lost or stolen card being spent.</summary>
    Task CancelAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>"GC-1234567890" / "SC-1234567890" (older cards: six digits), any case.</summary>
    public static bool LooksLikeCode(string? code) =>
        System.Text.RegularExpressions.Regex.IsMatch((code ?? "").Trim(), @"^(GC|SC)-\d{6,12}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
