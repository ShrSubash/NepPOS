using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Seeding;

/// <summary>
/// Dev-only demo products (called under #if DEBUG in MauiProgram).
/// Runs only when the Products table is empty, so it never touches
/// products created through PLU management.
/// Barcodes use the 200 prefix (GS1 in-store range) so they can never
/// collide with a real manufacturer barcode.
/// </summary>
public static class RegisterDbSeeder
{
    public static async Task SeedAsync(NepPosDbContext dbContext, ILogger logger)
    {
        try
        {
            if (await dbContext.Products.AnyAsync())
            {
                return;
            }

            dbContext.Products.AddRange(
                Demo("Heineken 6-Pack", 18.99m, "Beer", "101", "2000000001012", "heineken.png"),
                Demo("Corona Extra 6-Pack", 19.50m, "Beer", "102", "2000000001029", "corona.png"),
                Demo("Smirnoff Ice 4-Pack", 16.00m, "RTD", "103", "2000000001036", "smirnoffice.png"),
                Demo("Coca-Cola 375ml", 3.20m, "Soft Drink", "104", "2000000001043", "coke.png"),
                Demo("Jack Daniel's 700ml", 54.00m, "Spirits", "105", "2000000001050", "jackdaniels.png"),
                Demo("Yellow Tail Shiraz", 12.50m, "Wine", "106", "2000000001067", "shiraz.png"));

            await dbContext.SaveChangesAsync();
            logger.LogInformation("Register dev seed data created (6 demo products, PLU 101-106).");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Register seeding failed.");
        }
    }

    private static Product Demo(string name, decimal price, string category, string plu, string barcode, string image) => new()
    {
        Name = name,
        Price = price,
        Category = category,
        Plu = plu,
        Barcode = barcode,
        ImageUrl = $"images/products/{image}",
        IsActive = true
    };
}
