using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NepPOS.Infrastructure.Database;

/// <summary>
/// Design-time-only factory so 'dotnet ef migrations' can construct
/// a WorkforceDbContext without spinning up the full MAUI host.
/// Never used at runtime — MauiProgram.cs registers the real
/// DbContextOptions via AddRegisterDatabase instead.
/// </summary>
public sealed class WorkforceDbContextFactory : IDesignTimeDbContextFactory<WorkforceDbContext>
{
    public WorkforceDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<WorkforceDbContext>();
        optionsBuilder.UseSqlite("Data Source=neppos-register.design.db");
        return new WorkforceDbContext(optionsBuilder.Options);
    }
}