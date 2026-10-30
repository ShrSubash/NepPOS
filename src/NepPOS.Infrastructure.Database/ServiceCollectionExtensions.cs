using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepPOS.Application.Abstractions;

namespace NepPOS.Infrastructure.Database;

/// <summary>
/// Registers the Register-tier (SQLite) database services.
/// Per architecture Section 3: adding a new provider means one new registration
/// method here — no changes to Application or Domain.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRegisterDatabase(this IServiceCollection services, string sqliteConnectionString)
    {
        services.AddDbContext<NepPosDbContext>(options =>
            options.UseSqlite(sqliteConnectionString));

        services.AddDbContext<WorkforceDbContext>(options =>
    options.UseSqlite(sqliteConnectionString));

        services.AddScoped<ISaleTransactionRepository, SaleTransactionRepository>();

        return services;
    }
}