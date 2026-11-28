using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NepPOS.Contracts.Accounts;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Accounts;
using NepPOS.HeadOffice.Api.Data;
using NepPOS.HeadOffice.Api.Security;

namespace NepPOS.HeadOffice.Api.Endpoints;

/// <summary>
/// What a business's website does with customers, store accounts and gift cards (website key). It reads Head
/// Office's copy; whatever it changes is stored as a website event, applied to the copy straight away and passed
/// on to every register. A key only ever sees its own business.
/// </summary>
public static class WebsiteAccountEndpoints
{
    public static void MapWebsiteAccountEndpoints(this RouteGroupBuilder website)
    {
        website.MapGet("/customers/find", FindCustomerAsync);
        website.MapGet("/customers/purchases", PurchasesAsync);
        website.MapPost("/customers", SignUpAsync);
        website.MapGet("/customers/{id:guid}/account", StatementAsync);
        website.MapGet("/giftcards/{code}", GetGiftCardAsync);
        website.MapPost("/giftcards/store-credit", IssueStoreCreditAsync);
        website.MapPost("/giftcards/{code}/redeem", (string code, GiftCardChangeRequest request, HttpContext http, HeadOfficeDbContext db, CancellationToken ct) =>
            ChangeGiftCardAsync(code, request, spend: true, http, db, ct));
        website.MapPost("/giftcards/{code}/refund", (string code, GiftCardChangeRequest request, HttpContext http, HeadOfficeDbContext db, CancellationToken ct) =>
            ChangeGiftCardAsync(code, request, spend: false, http, db, ct));
    }

    /// <summary>By email (or phone): links a website account to the customer the register already knows.</summary>
    internal static async Task<IResult> FindCustomerAsync(string? email, string? phone, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        var normalisedEmail = (email ?? "").Trim().ToLowerInvariant();
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (normalisedEmail.Length == 0 && digits.Length < 6)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = new[] { "Give an email or a phone number." } });

        SharedCustomer? customer = null;
        if (normalisedEmail.Length > 0)
            customer = await db.SharedCustomers.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Email == normalisedEmail)
                .OrderByDescending(c => c.IsAccount).ThenBy(c => c.CreatedUtc)
                .FirstOrDefaultAsync(ct);
        if (customer is null && digits.Length >= 6)
            customer = (await db.SharedCustomers.AsNoTracking().Where(c => c.TenantId == tenantId && c.Phone != null).ToListAsync(ct))
                .FirstOrDefault(c => new string(c.Phone!.Where(char.IsDigit).ToArray()) == digits);

        return customer is null ? Results.NotFound() : Results.Ok(await ViewAsync(db, customer, ct));
    }

    /// <summary>
    /// In-store sales (and refunds) that had a customer on them, after change number <paramref name="after"/>, so the
    /// website can give points for shopping in store. Sales without a customer are skipped but still move <c>Next</c>
    /// on, so the website never reads them twice.
    /// </summary>
    internal static async Task<IResult> PurchasesAsync(long? after, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        var from = Math.Max(0, after ?? 0);
        var take = CustomerPurchaseChanges.PageSize;

        var rows = await db.ReceivedEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Type == SyncEventTypes.SaleCompleted && e.Id > from)
            .OrderBy(e => e.Id)
            .Take(take + 1)
            .Select(e => new { e.Id, e.StoreId, e.OccurredAtUtc, e.Payload })
            .ToListAsync(ct);
        var more = rows.Count > take;
        var page = rows.Take(take).ToList();

        var purchases = new List<CustomerPurchase>();
        foreach (var row in page)
        {
            SaleCompletedV1? sale;
            try { sale = JsonSerializer.Deserialize<SaleCompletedV1>(row.Payload, SyncJson.Options); }
            catch (JsonException) { continue; }   // checked when it arrived; never expected
            if (sale?.CustomerId is not { } customerId || customerId == Guid.Empty)
                continue;
            purchases.Add(new CustomerPurchase(row.Id, customerId, sale.SaleId, sale.InvoiceNumber, sale.Total,
                Utc(row.OccurredAtUtc), row.StoreId));
        }

        return Results.Ok(new CustomerPurchaseChanges(purchases, page.Count > 0 ? page[^1].Id : from, more));
    }

    /// <summary>A website sign-up becomes a customer at every register (or links to the one with the same email).</summary>
    internal static async Task<IResult> SignUpAsync(CustomerSignUp request, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        var name = (request.Name ?? "").Trim();
        if (!MailAddress.TryCreate(email, out _) || email.Length > 200)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = new[] { "Enter a valid email address." } });
        if (name.Length is 0 or > 200)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = new[] { "Enter the customer's name (up to 200 characters)." } });

        var existing = await db.SharedCustomers.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Email == email)
            .OrderByDescending(c => c.IsAccount).ThenBy(c => c.CreatedUtc)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return Results.Ok(await ViewAsync(db, existing, ct));

        string code;
        do code = "CUST-" + RandomNumberGenerator.GetInt32(10000000, 100000000);
        while (await db.SharedCustomers.AnyAsync(c => c.TenantId == tenantId && c.Code == code, ct));

        var now = DateTime.UtcNow;
        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim()[..Math.Min(30, request.Phone.Trim().Length)];
        var payload = new CustomerChangedV1(Guid.NewGuid(), code, name, phone, email, null, null, null, null,
            IsAccount: false, CreditLimit: 0, TermsDays: 30, IsActive: true, now, now);
        await RecordAsync(db, http, tenantId, Guid.NewGuid(), SyncEventTypes.CustomerChanged, payload, ct);
        await db.SaveChangesAsync(ct);

        var created = await SharedAccounts.FindCustomerAsync(db, tenantId, payload.CustomerId, ct);
        return Results.Ok(await ViewAsync(db, created!, ct));
    }

    /// <summary>The customer's store account: what they owe, how overdue, and every line (newest first).</summary>
    internal static async Task<IResult> StatementAsync(Guid id, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        var customer = await db.SharedCustomers.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.CustomerId == id, ct);
        if (customer is null)
            return Results.NotFound();

        var lines = await db.SharedAccountLines.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.CustomerId == id)
            .ToListAsync(ct);
        var ageing = Age(lines);
        return Results.Ok(new CustomerStatementView(View(customer, ageing),
            ageing,
            lines.OrderByDescending(l => l.AtUtc)
                .Select(l => new AccountStatementLine(l.EntryId, l.Kind, l.Amount, l.InvoiceNumber, Utc(l.AtUtc), l.DueUtc is { } d ? Utc(d) : null, l.Where, l.Note))
                .ToList()));
    }

    internal static async Task<IResult> GetGiftCardAsync(string code, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        var normalised = (code ?? "").Trim().ToUpperInvariant();
        var card = await db.SharedGiftCards.AsNoTracking().FirstOrDefaultAsync(g => g.TenantId == tenantId && g.Code == normalised, ct);
        return card is null
            ? Results.NotFound()
            : Results.Ok(new GiftCardView(card.Code, card.Kind, card.Balance, card.IsActive, card.LastUsedUtc is { } at ? Utc(at) : null));
    }

    /// <summary>
    /// A new store credit from the website (e.g. loyalty points turned into credit). It reaches every register like
    /// a credit issued at the counter. A retry with the same request id returns the same credit and changes nothing.
    /// </summary>
    internal static async Task<IResult> IssueStoreCreditAsync(StoreCreditIssueRequest request, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        if (request.RequestId == Guid.Empty)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["requestId"] = new[] { "Send a requestId (a new GUID per credit)." } });
        if (request.Amount <= 0 || request.Amount > StoreCreditIssueRequest.MaxAmount || decimal.Round(request.Amount, 2) != request.Amount)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = new[] { $"The amount must be more than $0 and at most {StoreCreditIssueRequest.MaxAmount:0}, in cents." } });

        // Already issued (a retry): the same credit again.
        var done = await db.SharedGiftCardLines.AsNoTracking().FirstOrDefaultAsync(l => l.EventId == request.RequestId && l.TenantId == tenantId, ct);
        if (done is not null)
        {
            var issued = await db.SharedGiftCards.AsNoTracking().FirstAsync(g => g.TenantId == tenantId && g.Code == done.Code, ct);
            return Results.Ok(new GiftCardView(issued.Code, issued.Kind, issued.Balance, issued.IsActive, issued.LastUsedUtc is { } used ? Utc(used) : null));
        }

        string code;
        do code = "SC-" + RandomNumberGenerator.GetInt32(10000, 100000) + RandomNumberGenerator.GetInt32(0, 100000).ToString("D5");   // as the registers make them
        while (await db.SharedGiftCards.AnyAsync(g => g.TenantId == tenantId && g.Code == code, ct));

        var reference = (request.Reference ?? "").Trim();
        var payload = new GiftCardMovedV1(code, "StoreCredit", request.Amount, null, null, DateTime.UtcNow,
            "Issued online " + (reference.Length > 150 ? reference[..150] : reference));
        await RecordAsync(db, http, tenantId, request.RequestId, SyncEventTypes.GiftCardMoved, payload, ct);
        await db.SaveChangesAsync(ct);

        var card = await db.SharedGiftCards.AsNoTracking().FirstAsync(g => g.TenantId == tenantId && g.Code == code, ct);
        return Results.Ok(new GiftCardView(card.Code, card.Kind, card.Balance, card.IsActive, card.LastUsedUtc is { } at ? Utc(at) : null));
    }

    /// <summary>
    /// Spends from (or puts back on) a card for an online order. A retry with the same request id changes nothing
    /// and gives the same answer; spending more than is on the card is refused.
    /// </summary>
    internal static async Task<IResult> ChangeGiftCardAsync(string code, GiftCardChangeRequest request, bool spend,
        HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        var normalised = (code ?? "").Trim().ToUpperInvariant();
        if (request.RequestId == Guid.Empty)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["requestId"] = new[] { "Send a requestId (a new GUID per change)." } });
        if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = new[] { "The amount must be more than $0, in cents." } });

        var card = await db.SharedGiftCards.FirstOrDefaultAsync(g => g.TenantId == tenantId && g.Code == normalised, ct);
        if (card is null)
            return Results.NotFound();

        // Already done (a retry): the same answer again.
        if (await db.SharedGiftCardLines.AnyAsync(l => l.EventId == request.RequestId, ct))
            return Results.Ok(new GiftCardChangeResult(card.Code, request.Amount, card.Balance));

        if (spend && !card.IsActive)
            return Results.Problem($"{card.Code} has been cancelled.", statusCode: 409);
        if (spend && card.Balance < request.Amount)
            return Results.Problem($"{card.Code} only has {card.Balance:0.00} left.", statusCode: 409);

        var reference = (request.Reference ?? "").Trim();
        var payload = new GiftCardMovedV1(card.Code, card.Kind, spend ? -request.Amount : request.Amount, null, null, DateTime.UtcNow,
            (spend ? "Spent online " : "Refunded online ") + (reference.Length > 150 ? reference[..150] : reference));
        await RecordAsync(db, http, tenantId, request.RequestId, SyncEventTypes.GiftCardMoved, payload, ct);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new GiftCardChangeResult(card.Code, request.Amount, card.Balance));
    }

    /// <summary>Stores the website's change as an event (for the registers) and applies it to the copy, in one save.</summary>
    private static async Task RecordAsync<T>(HeadOfficeDbContext db, HttpContext http, Guid tenantId, Guid eventId, string type, T payload, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToElement(payload, SyncJson.Options);
        db.WebsiteEvents.Add(new WebsiteEvent
        {
            EventId = eventId,
            TenantId = tenantId,
            WebsiteKeyId = WebsiteApiKeyHandler.KeyOf(http.User),
            Type = type,
            OccurredAtUtc = DateTime.UtcNow,
            Payload = json.GetRawText()
        });
        await SharedAccounts.ApplyAsync(db, tenantId, eventId, type, json, "Website", ct);
    }

    private static async Task<CustomerAccount> ViewAsync(HeadOfficeDbContext db, SharedCustomer customer, CancellationToken ct)
    {
        var lines = await db.SharedAccountLines.AsNoTracking()
            .Where(l => l.TenantId == customer.TenantId && l.CustomerId == customer.CustomerId)
            .ToListAsync(ct);
        return View(customer, Age(lines));
    }

    private static CustomerAccount View(SharedCustomer c, Ageing ageing) =>
        new(c.CustomerId, c.Code, c.Name, c.Email, c.Phone, c.IsAccount, c.CreditLimit, c.TermsDays, c.IsActive, c.Balance, Math.Max(0, ageing.Overdue));

    // Ageing is worked out in the business's local days; Head Office uses Sydney time, like the reports.
    private static Ageing Age(IEnumerable<SharedAccountLine> lines)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return AccountAgeing.Calculate(lines.Select(l => new AccountLine(l.Amount, Local(l.AtUtc), l.DueUtc is { } d ? Local(d) : null)),
            Local(DateTime.UtcNow));
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
