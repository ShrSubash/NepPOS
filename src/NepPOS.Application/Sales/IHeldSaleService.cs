using NepPOS.Domain.Entities;

namespace NepPOS.Application.Sales;

/// <summary>
/// Sales put on hold at this register (Hold), and picked up again (Recall). The cart is saved as the register's own
/// JSON, so nothing is lost if the register restarts in between.
/// </summary>
public interface IHeldSaleService
{
    Task<HeldSale> HoldAsync(string label, decimal total, int itemCount, string cartJson, string? employeeName, CancellationToken cancellationToken = default);

    /// <summary>Oldest first.</summary>
    Task<IReadOnlyList<HeldSale>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes it off hold and returns it (null if another register or a second tap already took it).</summary>
    Task<HeldSale?> RecallAsync(Guid id, CancellationToken cancellationToken = default);
}
