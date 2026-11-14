namespace NepPOS.Contracts.Reports;

// What the website (and later any owner app) reads from Head Office about store sales.
// Built only from sales the registers have sent, so a store that is offline shows up to its last sync.

/// <summary>GET /api/v1/stores: the business's stores and whether each is online.</summary>
public sealed record StoreSummary(
    Guid StoreId,
    string StoreCode,
    string Name,
    bool Online,
    DateTime? LastSeenUtc,
    DateTime? LastSaleUtc);

/// <summary>
/// GET /api/v1/reports/sales: one store's sales for trading days <paramref name="From"/> to
/// <paramref name="To"/> (a sale before 5 am counts for the day before, as on the register).
/// Money is AUD, GST inclusive unless the name says otherwise.
/// </summary>
public sealed record SalesReport(
    Guid StoreId,
    string StoreCode,
    string StoreName,
    DateOnly From,
    DateOnly To,
    int SaleCount,
    int ItemCount,
    decimal GrossSales,
    decimal Gst,
    decimal NetSales,
    decimal CashRounding,
    decimal TotalTaken,
    IReadOnlyList<NamedTotal> Tenders,
    IReadOnlyList<DayTotal> Days,
    IReadOnlyList<NamedTotal> Departments,
    IReadOnlyList<ProductSales> Products,
    IReadOnlyList<HourTotal> Hours,
    IReadOnlyList<NamedTotal> Employees);

/// <param name="Name">Tender, department or employee.</param>
/// <param name="Count">Transactions for tenders and employees; items for departments.</param>
/// <param name="Amount">AUD inc GST.</param>
public sealed record NamedTotal(string Name, int Count, decimal Amount);

public sealed record DayTotal(DateOnly Day, int SaleCount, decimal Amount);

public sealed record ProductSales(string Sku, string Name, int Quantity, decimal Amount);

/// <param name="Hour">0–23, the store's local time.</param>
/// <param name="SaleCount">Sales started in that hour.</param>
/// <param name="Amount">AUD inc GST.</param>
public sealed record HourTotal(int Hour, int SaleCount, decimal Amount);
