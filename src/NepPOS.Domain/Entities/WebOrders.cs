namespace NepPOS.Domain.Entities;

public enum WebOrderStatus
{
    Placed = 1,
    Picking = 2,
    Ready = 3,

    /// <summary>Collected or sent for delivery, and rung up as a sale (<see cref="WebOrder.SaleId"/>).</summary>
    Completed = 4,

    Cancelled = 5
}

/// <summary>
/// An order from the business's website for this store to pick. Its id is the website's order id, so the same order
/// arriving twice is the same row. Money: <see cref="Prepaid"/> was taken online, <see cref="OnAccount"/> goes on the
/// customer's account, and the rest is paid at the store.
/// </summary>
public sealed class WebOrder
{
    public Guid Id { get; set; }

    /// <summary>The website's order number, as the customer knows it.</summary>
    public string Reference { get; set; } = "";

    public string CustomerName { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }

    /// <summary>The NepPOS customer (shared by every store), when the website knows them.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>"Pickup" or "Delivery".</summary>
    public string Fulfilment { get; set; } = "Pickup";

    public string? Address { get; set; }
    public string? Note { get; set; }
    public DateTime PlacedUtc { get; set; }
    public DateTime? WantedUtc { get; set; }
    public decimal Total { get; set; }
    public decimal Prepaid { get; set; }
    public decimal OnAccount { get; set; }
    public WebOrderStatus Status { get; set; } = WebOrderStatus.Placed;
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Who's picking it (or picked it).</summary>
    public string? PickedBy { get; set; }

    public Guid? SaleId { get; set; }
    public long? InvoiceNumber { get; set; }
    public decimal? FinalTotal { get; set; }
    public decimal? RefundDue { get; set; }
    public string? CancelReason { get; set; }
    public List<WebOrderLine> Lines { get; set; } = new();

    public bool IsOpen => Status is WebOrderStatus.Placed or WebOrderStatus.Picking or WebOrderStatus.Ready;

    /// <summary>What's left to pay at the store for <paramref name="total"/>.</summary>
    public decimal DueAtStore(decimal total) => Math.Max(0, total - Prepaid - OnAccount);
}

/// <summary>One line of a website order. <see cref="Picked"/> stays null until someone has picked it.</summary>
public sealed class WebOrderLine
{
    public int Id { get; set; }
    public Guid WebOrderId { get; set; }

    /// <summary>Its position in the website's order (0, 1, 2…), which is how the website hears what was picked.</summary>
    public int LineNumber { get; set; }

    /// <summary>The NepPOS product (its <see cref="Product.SyncId"/>), or null for a non-stock line such as a delivery fee.</summary>
    public Guid? ProductSyncId { get; set; }

    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public int? Picked { get; set; }

    public int Supplied => Picked ?? Quantity;
}

public enum StockTransferStatus
{
    /// <summary>Sent from here, or on its way here.</summary>
    InTransit = 1,

    Received = 2
}

/// <summary>
/// Stock sent between two of the owner's stores. The sending store and the receiving store each keep a copy with the
/// same <see cref="Id"/>; stock leaves the sender when it's sent and arrives at the receiver when it's counted in.
/// </summary>
public sealed class StockTransfer
{
    public Guid Id { get; set; }

    /// <summary>"TR-STORE-001-00004".</summary>
    public string Number { get; set; } = "";

    public string FromStore { get; set; } = "";
    public string ToStore { get; set; } = "";

    /// <summary>True when this store sent it.</summary>
    public bool Outgoing { get; set; }

    public StockTransferStatus Status { get; set; } = StockTransferStatus.InTransit;
    public DateTime SentUtc { get; set; }
    public DateTime? ReceivedUtc { get; set; }
    public string? SentBy { get; set; }
    public string? ReceivedBy { get; set; }
    public string? Note { get; set; }
    public List<StockTransferLine> Lines { get; set; } = new();

    public decimal Value => Lines.Sum(l => l.Quantity * l.UnitCost);
}

/// <summary>One product on a transfer, in singles at the sender's cost.</summary>
public sealed class StockTransferLine
{
    public int Id { get; set; }
    public Guid StockTransferId { get; set; }
    public int LineNumber { get; set; }
    public Guid ProductSyncId { get; set; }
    public string ProductName { get; set; } = "";
    public string? Barcode { get; set; }
    public string? Sku { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }

    /// <summary>What arrived (null until counted in).</summary>
    public decimal? Received { get; set; }
}
