using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NepPOS.Infrastructure.Database;

public sealed class NepPosDbContextFactory : IDesignTimeDbContextFactory<NepPosDbContext>
{
    public NepPosDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<NepPosDbContext>();
        optionsBuilder.UseSqlite("Data Source=neppos-register.design.db");
        return new NepPosDbContext(optionsBuilder.Options);
    }
}