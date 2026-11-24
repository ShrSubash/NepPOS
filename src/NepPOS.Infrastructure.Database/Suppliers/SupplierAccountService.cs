using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Suppliers;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Suppliers;

/// <summary>Suppliers and what this store owes them (creditors). Kept at this store.</summary>
public sealed class SupplierAccountService : ISupplierAccountService
{
    private readonly NepPosDbContext _db;

    public SupplierAccountService(NepPosDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<IReadOnlyList<CreditorRow>> ListAsync(string? search = null, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        var query = _db.Suppliers.AsNoTracking();
        if (!includeInactive)
            query = query.Where(s => s.IsActive);
        var text = (search ?? "").Trim();
        if (text.Length > 0)
            query = query.Where(s => EF.Functions.Like(s.Name, $"%{text}%") || EF.Functions.Like(s.AccountNumber ?? "", $"%{text}%"));

        var suppliers = await query.OrderBy(s => s.Name).Take(500).ToListAsync(cancellationToken);
        var ids = suppliers.Select(s => s.Id).ToList();
        var entries = (await _db.SupplierAccountEntries.AsNoTracking()
                .Where(e => ids.Contains(e.SupplierId))
                .ToListAsync(cancellationToken))
            .ToLookup(e => e.SupplierId);

        var today = DateTime.Today;
        return suppliers
            .Select(s =>
            {
                var own = entries[s.Id].ToList();
                return new CreditorRow(s, SupplierAgeing.For(own, today), NextDue(own));
            })
            .OrderByDescending(r => r.Ageing.Total != 0)
            .ThenBy(r => r.Supplier.Name)
            .ToList();
    }

    public async Task<SupplierStatement?> StatementAsync(int supplierId, CancellationToken cancellationToken = default)
    {
        var supplier = await _db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken);
        if (supplier is null)
            return null;
        var entries = await _db.SupplierAccountEntries.AsNoTracking()
            .Where(e => e.SupplierId == supplierId)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .ToListAsync(cancellationToken);
        return new SupplierStatement(supplier, entries, SupplierAgeing.For(entries, DateTime.Today));
    }

    public async Task<Supplier> SaveAsync(SupplierInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("Enter the supplier's name.");
        if (input.TermsDays is < 0 or > 365)
            throw new InvalidOperationException("Payment terms are 0 to 365 days.");
        var email = Blank(input.Email);
        if (email is not null && !MailAddress.TryCreate(email, out _))
            throw new InvalidOperationException("That email address doesn't look right.");

        Supplier supplier;
        if (input.Id is { } id)
        {
            supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                ?? throw new InvalidOperationException("That supplier no longer exists.");
        }
        else
        {
            if (await _db.Suppliers.AnyAsync(s => s.Name == name, cancellationToken))
                throw new InvalidOperationException($"There's already a supplier called {name}.");
            supplier = new Supplier();
            _db.Suppliers.Add(supplier);
        }

        supplier.Name = name.Length > 200 ? name[..200] : name;
        supplier.AccountNumber = Cut(Blank(input.AccountNumber), 50);
        supplier.ContactName = Cut(Blank(input.ContactName), 200);
        supplier.Phone = Cut(Blank(input.Phone), 30);
        supplier.Email = Cut(email, 200);
        supplier.Abn = Cut(Blank(input.Abn), 20);
        supplier.Address = Cut(Blank(input.Address), 300);
        supplier.Notes = Cut(Blank(input.Notes), 1000);
        supplier.TermsDays = input.TermsDays;
        supplier.IsActive = input.IsActive;

        await _db.SaveChangesAsync(cancellationToken);
        return supplier;
    }

    public async Task AddBillAsync(int supplierId, string reference, DateOnly invoiceDate, decimal amount, string? note, string? employeeName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new InvalidOperationException("Enter the supplier's invoice number.");
        if (amount <= 0)
            throw new InvalidOperationException("Enter the invoice total.");
        var supplier = await FindAsync(supplierId, cancellationToken);
        SupplierLedger.Add(_db, supplier, SupplierEntryKind.Bill, amount, reference, invoiceDate, note, employeeName);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddPaymentAsync(int supplierId, decimal amount, DateOnly date, string? reference, string? note, string? employeeName, CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
            throw new InvalidOperationException("Enter the amount paid.");
        var supplier = await FindAsync(supplierId, cancellationToken);
        SupplierLedger.Add(_db, supplier, SupplierEntryKind.Payment, -amount, reference, date, note, employeeName);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddCreditAsync(int supplierId, decimal amount, DateOnly date, string? reference, string? note, string? employeeName, CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
            throw new InvalidOperationException("Enter the credit amount.");
        var supplier = await FindAsync(supplierId, cancellationToken);
        SupplierLedger.Add(_db, supplier, SupplierEntryKind.Credit, -amount, reference, date, note, employeeName);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Supplier> FindAsync(int supplierId, CancellationToken cancellationToken) =>
        await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken)
        ?? throw new InvalidOperationException("That supplier no longer exists.");

    /// <summary>The earliest due date of a bill that isn't fully paid (oldest bills are paid first).</summary>
    private static DateOnly? NextDue(IReadOnlyCollection<SupplierAccountEntry> entries)
    {
        var paid = entries.Where(e => e.Amount < 0).Sum(e => -e.Amount);
        foreach (var bill in entries.Where(e => e.Amount > 0).OrderBy(e => e.DueDate ?? e.Date))
        {
            if (paid >= bill.Amount)
            {
                paid -= bill.Amount;
                continue;
            }
            return bill.DueDate ?? bill.Date;
        }
        return null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

/// <summary>Adds a line to a supplier's account and keeps their balance in step (used by receiving too).</summary>
internal static class SupplierLedger
{
    public static SupplierAccountEntry Add(NepPosDbContext db, Supplier supplier, SupplierEntryKind kind, decimal amount,
        string? reference, DateOnly date, string? note, string? employeeName, int? stockReceiptId = null)
    {
        var entry = new SupplierAccountEntry
        {
            SupplierId = supplier.Id,
            Kind = kind,
            Amount = Math.Round(amount, 2),
            Reference = Cut(reference?.Trim(), 100),
            StockReceiptId = stockReceiptId,
            Date = date,
            DueDate = kind == SupplierEntryKind.Bill ? date.AddDays(Math.Max(0, supplier.TermsDays)) : null,
            Note = Cut(string.IsNullOrWhiteSpace(note) ? null : note.Trim(), 300),
            EmployeeName = Cut(employeeName, 200),
            RecordedUtc = DateTime.UtcNow
        };
        db.SupplierAccountEntries.Add(entry);
        supplier.Balance += entry.Amount;
        return entry;
    }

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
