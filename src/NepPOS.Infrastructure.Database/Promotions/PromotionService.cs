using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Promotions;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Promotions;

public sealed class PromotionService : IPromotionService
{
    private readonly NepPosDbContext _db;

    public PromotionService(NepPosDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<IReadOnlyList<Promotion>> ListAsync(CancellationToken cancellationToken = default) =>
        await _db.Promotions.AsNoTracking().OrderByDescending(p => p.IsActive).ThenBy(p => p.Name).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Promotion>> ActiveAsync(CancellationToken cancellationToken = default) =>
        await _db.Promotions.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Id).ToListAsync(cancellationToken);

    public async Task<Promotion> SaveAsync(Promotion promotion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        var name = (promotion.Name ?? "").Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("Give the promotion a name (it prints on the receipt, e.g. \"2 for $10\").");
        if (string.IsNullOrWhiteSpace(promotion.Category) && promotion.ProductIdList.Count == 0)
            throw new InvalidOperationException("Choose the products, or a department.");
        var problem = promotion.Kind switch
        {
            PromotionKind.MultiBuy when promotion.BuyQuantity < 2 || promotion.Price <= 0 => "A multi-buy needs a quantity of 2 or more and a price.",
            PromotionKind.PercentOff when promotion.Percent is <= 0 or > 100 => "Enter a % between 1 and 100.",
            PromotionKind.AmountOff when promotion.Amount <= 0 => "Enter how much comes off each item.",
            _ => null
        };
        if (problem is not null)
            throw new InvalidOperationException(problem);
        if (promotion.StartDate is { } start && promotion.EndDate is { } end && end < start)
            throw new InvalidOperationException("The end date is before the start date.");
        if ((promotion.FromTime is null) != (promotion.ToTime is null))
            throw new InvalidOperationException("Set both times for a happy hour, or neither.");

        Promotion target;
        if (promotion.Id == 0)
        {
            target = new Promotion();
            _db.Promotions.Add(target);
        }
        else
        {
            target = await _db.Promotions.FirstOrDefaultAsync(p => p.Id == promotion.Id, cancellationToken)
                ?? throw new InvalidOperationException("That promotion no longer exists.");
        }

        target.Name = name.Length > 100 ? name[..100] : name;
        target.Kind = promotion.Kind;
        target.BuyQuantity = promotion.BuyQuantity;
        target.Price = promotion.Price;
        target.Percent = promotion.Percent;
        target.Amount = promotion.Amount;
        target.Category = string.IsNullOrWhiteSpace(promotion.Category) ? null : promotion.Category.Trim();
        target.ProductIds = target.Category is null ? string.Join(',', promotion.ProductIdList) : "";
        target.StartDate = promotion.StartDate;
        target.EndDate = promotion.EndDate;
        target.Days = promotion.Days ?? "";
        target.FromTime = promotion.FromTime;
        target.ToTime = promotion.ToTime;
        target.IsActive = promotion.IsActive;
        await _db.SaveChangesAsync(cancellationToken);
        return target;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var promotion = await _db.Promotions.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (promotion is null) return;
        _db.Promotions.Remove(promotion);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
