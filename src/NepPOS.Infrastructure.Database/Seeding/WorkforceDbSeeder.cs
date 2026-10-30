using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Authentication;

namespace NepPOS.Infrastructure.Database.Seeding;

public static class WorkforceDbSeeder
{
    public static async Task SeedAsync(WorkforceDbContext dbContext, ILogger logger)
    {
        try
        {
            var alreadySeeded = await dbContext.Employees.AnyAsync();
            if (alreadySeeded)
            {
                return;
            }

            var (ownerHash, ownerSalt) = PinHasher.Hash("123456");
            var (cashierHash, cashierSalt) = PinHasher.Hash("1111");

            dbContext.Employees.AddRange(
                new Employee { FullName = "Store Owner", Role = EmployeeRole.Owner, PinHash = ownerHash, PinSalt = ownerSalt, IsActive = true },
                new Employee { FullName = "Test Cashier", Role = EmployeeRole.Cashier, PinHash = cashierHash, PinSalt = cashierSalt, IsActive = true });

            await dbContext.SaveChangesAsync();
            logger.LogInformation("Workforce seed data created: Owner PIN 123456, Cashier PIN 1111.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workforce seeding failed.");
        }
    }
}