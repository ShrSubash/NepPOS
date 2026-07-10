using Microsoft.EntityFrameworkCore;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database;

/// <summary>
/// EF Core context for the Register tier (SQLite). Master and Cloud tiers use the
/// same entity configurations against SQL Server via a separate provider registration
/// in Infrastructure.Config's DI setup — this class itself is provider-agnostic.
/// </summary>
public sealed class NepPosDbContext : DbContext
{
    public DbSet<SaleTransaction> SaleTransactions => Set<SaleTransaction>();
    public DbSet<SaleLineItem> SaleLineItems => Set<SaleLineItem>();

    public NepPosDbContext(DbContextOptions<NepPosDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SaleTransaction>(entity =>
        {
            entity.HasKey(st => st.Id);
            entity.Property(st => st.StoreId).IsRequired().HasMaxLength(50);
            entity.Property(st => st.RegisterId).IsRequired().HasMaxLength(50);
            entity.Property(st => st.Status).HasConversion<string>().HasMaxLength(20);

            // Invoice numbers must be gapless and unique per store (Section 6) — enforce
            // at the database level, not just in application code, since this is a
            // correctness invariant that must hold even under concurrent writes.
            entity.HasIndex(st => new { st.StoreId, st.InvoiceNumber }).IsUnique()
                .HasFilter("[InvoiceNumber] IS NOT NULL");

            // EF Core can't map a private backing field + public IReadOnlyCollection
            // without being told explicitly which field backs the navigation.
            entity.Metadata.FindNavigation(nameof(SaleTransaction.LineItems))!
    .SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<SaleLineItem>(entity =>
        {
            entity.HasKey(li => li.Id);
            entity.Property(li => li.Sku).IsRequired().HasMaxLength(50);
            entity.Property(li => li.ProductName).IsRequired().HasMaxLength(200);
            entity.Property(li => li.UnitPriceIncGst).HasPrecision(18, 4);
            entity.Property(li => li.GstRate).HasPrecision(5, 4);
        });
    }
}