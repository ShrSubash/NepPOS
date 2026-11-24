using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Customers;
using NepPOS.Domain.Entities;
using NepPOS.Infrastructure.Database.Outbox;

namespace NepPOS.Infrastructure.Database.Customers;

/// <summary>Customers and their accounts on this register; every change also goes to Head Office for the other stores.</summary>
public sealed class CustomerService : ICustomerService
{
    private readonly NepPosDbContext _db;
    private readonly RegisterTerminal _terminal;

    public CustomerService(NepPosDbContext db, RegisterTerminal terminal)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
    }

    public async Task<IReadOnlyList<Customer>> SearchAsync(string? term, bool accountsOnly = false, int take = 50, CancellationToken cancellationToken = default)
    {
        var query = _db.Customers.AsNoTracking();
        if (accountsOnly)
            query = query.Where(c => c.IsAccount);

        var text = (term ?? "").Trim();
        if (text.Length > 0)
        {
            var upper = text.ToUpperInvariant();
            var digits = new string(text.Where(char.IsDigit).ToArray());
            // SQLite LIKE ignores case for ASCII, so names match however they're typed.
            query = query.Where(c =>
                EF.Functions.Like(c.Name, $"%{text}%") ||
                EF.Functions.Like(c.Company ?? "", $"%{text}%") ||
                EF.Functions.Like(c.Email ?? "", $"%{text}%") ||
                c.Code == upper ||
                (digits.Length >= 3 && (c.Phone ?? "").Replace(" ", "").Contains(digits)));
        }

        // A search lists by name; no search shows the latest added or edited first.
        var ordered = text.Length > 0
            ? query.OrderByDescending(c => c.IsActive).ThenBy(c => c.Name)
            : query.OrderByDescending(c => c.IsActive).ThenByDescending(c => c.UpdatedUtc);
        return await ordered.Take(Math.Clamp(take, 1, 500)).ToListAsync(cancellationToken);
    }

    public Task<Customer?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<Customer?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalised = (code ?? "").Trim().ToUpperInvariant();
        return _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Code == normalised, cancellationToken);
    }

    public async Task<Customer> SaveAsync(CustomerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("Enter the customer's name.");
        if (input.CreditLimit < 0)
            throw new InvalidOperationException("The credit limit can't be negative.");
        if (input.TermsDays is < 0 or > 365)
            throw new InvalidOperationException("Payment terms are 0 to 365 days.");
        var email = Blank(input.Email);
        if (email is not null && !MailAddress.TryCreate(email, out _))
            throw new InvalidOperationException("That email address doesn't look right.");

        var now = DateTime.UtcNow;
        Customer customer;
        if (input.Id is { } id)
        {
            customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                ?? throw new InvalidOperationException("That customer no longer exists.");
            if (!input.IsAccount && customer.IsAccount && customer.Balance != 0)
                throw new InvalidOperationException($"{customer.Name} still has {customer.Balance:C} on their account. Clear it before turning the account off.");
        }
        else
        {
            customer = new Customer { Id = Guid.NewGuid(), Code = await NewCodeAsync(cancellationToken), CreatedUtc = now };
            _db.Customers.Add(customer);
        }

        customer.Name = name.Length > 200 ? name[..200] : name;
        customer.Phone = Cut(Blank(input.Phone), 30);
        customer.Email = Cut(email, 200);
        customer.Company = Cut(Blank(input.Company), 200);
        customer.Abn = Cut(Blank(input.Abn), 20);
        customer.Address = Cut(Blank(input.Address), 300);
        customer.Notes = Cut(Blank(input.Notes), 1000);
        customer.IsAccount = input.IsAccount;
        customer.CreditLimit = Math.Round(input.CreditLimit, 2);
        customer.TermsDays = input.TermsDays;
        customer.IsActive = input.IsActive;
        customer.UpdatedUtc = now;

        _db.OutboxMessages.Add(OutboxMessage.CustomerChanged(customer, _terminal.StoreId, _terminal.RegisterId));
        await _db.SaveChangesAsync(cancellationToken);
        return customer;
    }

    public async Task<CustomerStatement?> StatementAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var customer = await GetAsync(id, cancellationToken);
        if (customer is null)
            return null;
        var entries = await _db.CustomerAccountEntries.AsNoTracking()
            .Where(e => e.CustomerId == id)
            .ToListAsync(cancellationToken);
        entries = entries.OrderByDescending(e => e.AtUtc).ToList();
        return new CustomerStatement(customer, entries, CustomerAgeing.For(entries, DateTime.Now));
    }

    public async Task<IReadOnlyList<DebtorRow>> DebtorsAsync(CancellationToken cancellationToken = default)
    {
        // (Balances aren't compared in SQL: SQLite keeps money as text.)
        var customers = await _db.Customers.AsNoTracking()
            .Where(c => c.IsAccount || _db.CustomerAccountEntries.Any(e => e.CustomerId == c.Id))
            .ToListAsync(cancellationToken);
        var ids = customers.Select(c => c.Id).ToList();
        var entries = (await _db.CustomerAccountEntries.AsNoTracking()
                .Where(e => ids.Contains(e.CustomerId))
                .ToListAsync(cancellationToken))
            .ToLookup(e => e.CustomerId);

        var now = DateTime.Now;
        return customers
            .Select(c => new DebtorRow(c, CustomerAgeing.For(entries[c.Id], now),
                entries[c.Id].Where(e => e.Kind == AccountEntryKind.Payment).Select(e => (DateTime?)e.AtUtc).Max()))
            .Where(r => r.Ageing.Total != 0)
            .OrderByDescending(r => r.Ageing.Overdue)
            .ThenByDescending(r => r.Ageing.Total)
            .ToList();
    }

    public async Task AdjustAsync(Guid id, decimal amount, string reason, string? employeeName, CancellationToken cancellationToken = default)
    {
        reason = (reason ?? "").Trim();
        if (amount == 0)
            throw new InvalidOperationException("Enter the amount to adjust by.");
        if (reason.Length == 0)
            throw new InvalidOperationException("Say why the account is being adjusted.");

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("That customer no longer exists.");

        var entry = new CustomerAccountEntry
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Kind = AccountEntryKind.Adjustment,
            Amount = Math.Round(amount, 2),
            AtUtc = DateTime.UtcNow,
            Where = $"{_terminal.StoreId} / {_terminal.RegisterId}",
            Note = Cut(string.IsNullOrWhiteSpace(employeeName) ? reason : $"{reason} ({employeeName.Trim()})", 300)!
        };
        _db.CustomerAccountEntries.Add(entry);
        customer.Balance += entry.Amount;
        _db.OutboxMessages.Add(OutboxMessage.AccountEntry(entry, _terminal.StoreId, _terminal.RegisterId));
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> NewCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            // Eight random digits: unique across stores without asking Head Office first.
            var code = "CUST-" + RandomNumberGenerator.GetInt32(10000000, 100000000);
            if (!await _db.Customers.AnyAsync(c => c.Code == code, cancellationToken))
                return code;
        }
        throw new InvalidOperationException("Couldn't find a free customer number. Try again.");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
