namespace NepPOS.Presentation.Register.Components.Layout;

/// <summary>One page in Manage. A null <see cref="Href"/> means it isn't built yet (shown as "Soon").</summary>
public sealed record ManageItem(string Label, string Description, string? Href, string Icon, params string[] AlsoActiveOn);

/// <summary>A group in the Manage sidebar.</summary>
public sealed record ManageSection(string Title, IReadOnlyList<ManageItem> Items);

/// <summary>
/// Everything in Manage, in sidebar order. The sidebar and the Overview page both read this,
/// so a new settings page is added here once.
/// </summary>
public static class ManageNav
{
    public static readonly ManageItem Overview = new("Overview", "Everything in one place", "manage", "grid");

    public static readonly IReadOnlyList<ManageSection> Sections = new[]
    {
        new ManageSection("Products", new[]
        {
            new ManageItem("Products & PLUs", "Add products, prices, barcodes and pack sizes", "manage/plu", "tag"),
            new ManageItem("Receive stock", "Supplier deliveries: stock in, costs and margins", "manage/receiving", "truck"),
            new ManageItem("Suppliers", "Contacts, payment terms and what you owe them", "manage/suppliers", "building"),
            new ManageItem("Wastage", "Ullages: broken, expired or used stock written off", "manage/wastage", "trash"),
        }),
        new ManageSection("Sales", new[]
        {
            new ManageItem("Reports & End of Day", "Today's takings, X and Z reports, PDF and CSV", "manage/reports", "chart", "manage/end-of-day"),
            new ManageItem("Journal", "Every sale, void, refund and drawer open", "manage/journal", "book"),
            new ManageItem("Payment types", "Tenders, gift cards, store credit and return rules", "manage/payment-types", "wallet"),
            new ManageItem("Gift cards & credit", "Balances and history of every card", "manage/gift-cards", "card"),
        }),
        new ManageSection("Customers", new[]
        {
            new ManageItem("Customers", "Customers and accounts, shared by all your stores", "manage/customers", "users"),
            new ManageItem("Debtors", "Who owes you, and how overdue", "manage/debtors", "receipt"),
        }),
        new ManageSection("Store setup", new[]
        {
            new ManageItem("Receipt & printer", "Store details on receipts, printer and cash drawer", "manage/receipt-settings", "printer"),
            new ManageItem("Email", "Who gets the end-of-day report by email", "manage/email", "mail"),
            new ManageItem("Surcharges", "Sunday and public holiday surcharges", "manage/surcharges", "percent"),
        }),
        new ManageSection("Connections", new[]
        {
            new ManageItem("Head Office", "Send sales and products to Head Office and the website", "manage/head-office", "cloud"),
        }),
    };

    /// <summary>Line icons (24×24, stroke = currentColor).</summary>
    public static string Svg(string icon) =>
        "<svg viewBox=\"0 0 24 24\" width=\"20\" height=\"20\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">"
        + icon switch
        {
            "grid" => "<rect width=\"7\" height=\"7\" x=\"3\" y=\"3\" rx=\"1.5\"/><rect width=\"7\" height=\"7\" x=\"14\" y=\"3\" rx=\"1.5\"/><rect width=\"7\" height=\"7\" x=\"14\" y=\"14\" rx=\"1.5\"/><rect width=\"7\" height=\"7\" x=\"3\" y=\"14\" rx=\"1.5\"/>",
            "tag" => "<path d=\"M12.6 2.6A2 2 0 0 0 11.2 2H4a2 2 0 0 0-2 2v7.2a2 2 0 0 0 .6 1.4l8.7 8.7a2.4 2.4 0 0 0 3.4 0l6.6-6.6a2.4 2.4 0 0 0 0-3.4z\"/><circle cx=\"7.5\" cy=\"7.5\" r=\"1\"/>",
            "truck" => "<path d=\"M14 18V6a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2v11a1 1 0 0 0 1 1h2\"/><path d=\"M15 18H9\"/><path d=\"M19 18h2a1 1 0 0 0 1-1v-3.6a1 1 0 0 0-.2-.6l-3.5-4.4a1 1 0 0 0-.8-.4H14\"/><circle cx=\"17\" cy=\"18\" r=\"2\"/><circle cx=\"7\" cy=\"18\" r=\"2\"/>",
            "chart" => "<path d=\"M3 3v16a2 2 0 0 0 2 2h16\"/><path d=\"M18 17V9\"/><path d=\"M13 17V5\"/><path d=\"M8 17v-3\"/>",
            "book" => "<path d=\"M4 19.5v-15A2.5 2.5 0 0 1 6.5 2H20v20H6.5a2.5 2.5 0 0 1 0-5H20\"/><path d=\"M8 7h8\"/><path d=\"M8 11h6\"/>",
            "wallet" => "<path d=\"M19 7V4a1 1 0 0 0-1-1H5a2 2 0 0 0 0 4h15a1 1 0 0 1 1 1v4h-3a2 2 0 0 0 0 4h3a1 1 0 0 0 1-1v-2a1 1 0 0 0-1-1\"/><path d=\"M3 5v14a2 2 0 0 0 2 2h15a1 1 0 0 0 1-1v-4\"/>",
            "users" => "<path d=\"M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2\"/><circle cx=\"9\" cy=\"7\" r=\"4\"/><path d=\"M22 21v-2a4 4 0 0 0-3-3.9\"/><path d=\"M16 3.1a4 4 0 0 1 0 7.8\"/>",
            "percent" => "<line x1=\"19\" x2=\"5\" y1=\"5\" y2=\"19\"/><circle cx=\"6.5\" cy=\"6.5\" r=\"2.5\"/><circle cx=\"17.5\" cy=\"17.5\" r=\"2.5\"/>",
            "card" => "<rect width=\"20\" height=\"14\" x=\"2\" y=\"5\" rx=\"2\"/><path d=\"M2 10h20\"/>",
            "printer" => "<path d=\"M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2\"/><path d=\"M6 9V3a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v6\"/><rect x=\"6\" y=\"14\" width=\"12\" height=\"8\" rx=\"1\"/>",
            "mail" => "<rect width=\"20\" height=\"16\" x=\"2\" y=\"4\" rx=\"2\"/><path d=\"m22 7-9 5.7a2 2 0 0 1-2 0L2 7\"/>",
            "cloud" => "<path d=\"M17.5 19H9a7 7 0 1 1 6.7-9h1.8a4.5 4.5 0 1 1 0 9Z\"/>",
            "building" => "<rect x=\"4\" y=\"2\" width=\"16\" height=\"20\" rx=\"2\"/><path d=\"M9 22v-4h6v4\"/><path d=\"M8 6h.01\"/><path d=\"M12 6h.01\"/><path d=\"M16 6h.01\"/><path d=\"M8 10h.01\"/><path d=\"M12 10h.01\"/><path d=\"M16 10h.01\"/><path d=\"M8 14h.01\"/><path d=\"M12 14h.01\"/><path d=\"M16 14h.01\"/>",
            "receipt" => "<path d=\"M4 2v20l2-1 2 1 2-1 2 1 2-1 2 1 2-1 2 1V2l-2 1-2-1-2 1-2-1-2 1-2-1-2 1Z\"/><path d=\"M16 8h-6a2 2 0 1 0 0 4h4a2 2 0 1 1 0 4H8\"/><path d=\"M12 17.5v-11\"/>",
            "trash" => "<path d=\"M3 6h18\"/><path d=\"M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6\"/><path d=\"M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2\"/><path d=\"M10 11v6\"/><path d=\"M14 11v6\"/>",
            "back" => "<path d=\"m12 19-7-7 7-7\"/><path d=\"M19 12H5\"/>",
            _ => "<circle cx=\"12\" cy=\"12\" r=\"9\"/>"
        }
        + "</svg>";
}
