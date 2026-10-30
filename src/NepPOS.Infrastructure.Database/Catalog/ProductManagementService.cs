using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Catalog;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Catalog;

public sealed class ProductManagementService : IProductManagementService
{
    private readonly NepPosDbContext _dbContext;

    public ProductManagementService(NepPosDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<Product>> GetAllAsync() =>
        await _dbContext.Products.OrderBy(p => p.Name).ToListAsync();

    public async Task<Product?> GetByIdAsync(int id) =>
        await _dbContext.Products.FindAsync(id);

    public async Task<Product> CreateAsync(Product product)
    {
        _dbContext.Products.Add(product);
        await _dbContext.SaveChangesAsync();
        return product;
    }

    public async Task UpdateAsync(Product product)
    {
        _dbContext.Products.Update(product);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DeactivateAsync(int id)
    {
        var product = await _dbContext.Products.FindAsync(id);
        if (product is null) return;
        product.IsActive = false;
        await _dbContext.SaveChangesAsync();
    }
}