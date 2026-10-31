using Microsoft.EntityFrameworkCore;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database;

/// <summary>
/// EF Core context for the Register tier (SQLite). Master and Cloud tiers use the
/// same entity configurations against SQL Server via a separate provider registration
/// in ServiceCollectionExtensions — this class itself is provider-agnostic.
/// </summary>
public sealed class NepPosDbContext : DbContext
{
    public DbSet<SaleTransaction> SaleTransactions => Set<SaleTransaction>();
    public DbSet<SaleLineItem> SaleLineItems => Set<SaleLineItem>();
    public DbSet<SalePayment> SalePayments => Set<SalePayment>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();

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

            entity.Property(st => st.CashRounding).HasPrecision(18, 2);
            entity.Property(st => st.EmployeeName).HasMaxLength(200);

            entity.HasMany(st => st.Payments)
                  .WithOne()
                  .HasForeignKey(p => p.SaleTransactionId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.Metadata.FindNavigation(nameof(SaleTransaction.Payments))!
                  .SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<SalePayment>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.TenderType).HasConversion<string>().HasMaxLength(20);
            entity.Property(p => p.Amount).HasPrecision(18, 2);
            entity.Property(p => p.Tendered).HasPrecision(18, 2);
            entity.Property(p => p.Change).HasPrecision(18, 2);
            entity.Property(p => p.AuthCode).HasMaxLength(20);
            entity.Property(p => p.CardType).HasMaxLength(20);
            entity.Property(p => p.MaskedPan).HasMaxLength(25);
            entity.Property(p => p.ResponseText).HasMaxLength(100);
            entity.Property(p => p.CustomerReceipt).HasMaxLength(4000);
        });

        modelBuilder.Entity<SaleLineItem>(entity =>
        {
            entity.HasKey(li => li.Id);
            entity.Property(li => li.Sku).IsRequired().HasMaxLength(50);
            entity.Property(li => li.ProductName).IsRequired().HasMaxLength(200);
            entity.Property(li => li.UnitPriceIncGst).HasPrecision(18, 4);
            entity.Property(li => li.GstRate).HasPrecision(5, 4);
        });

        modelBuilder.Entity<JournalEntry>(entity =>
        {
            entity.HasKey(j => j.Id);
            entity.Property(j => j.StoreId).IsRequired().HasMaxLength(50);
            entity.Property(j => j.RegisterId).IsRequired().HasMaxLength(50);
            entity.Property(j => j.EmployeeName).HasMaxLength(200);
            entity.Property(j => j.EventType).HasConversion<string>().HasMaxLength(40);
            entity.Property(j => j.Amount).HasPrecision(18, 2);
            entity.Property(j => j.Detail).IsRequired().HasMaxLength(500);

            // SQLite returns DateTime without a Kind; everything here is stored as UTC.
            entity.Property(j => j.OccurredAtUtc)
                  .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            // One gapless sequence per register, enforced by the database.
            entity.HasIndex(j => new { j.StoreId, j.RegisterId, j.Sequence }).IsUnique();
            entity.HasIndex(j => j.OccurredAtUtc);
        });

        modelBuilder.Entity<ProductVariant>(entity =>
        {
            entity.HasOne(v => v.Product)
                  .WithMany()
                  .HasForeignKey(v => v.ProductId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(v => v.Barcode);
            entity.HasIndex(v => v.Plu);
        });
    }
}
