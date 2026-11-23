using Microsoft.EntityFrameworkCore;
using NepPOS.Domain.Entities;
using NepPOS.Infrastructure.Database.Outbox;

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
    public DbSet<ProductBarcode> ProductBarcodes => Set<ProductBarcode>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<ProductSupplierCode> ProductSupplierCodes => Set<ProductSupplierCode>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

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
            entity.Property(li => li.Category).HasMaxLength(100);
            entity.Ignore(li => li.StockUnits);
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

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            // Id is the SQLite row number: assigned on insert, never reused (rows are never deleted).
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Id).ValueGeneratedOnAdd();
            entity.Property(o => o.Type).IsRequired().HasMaxLength(50);
            entity.Property(o => o.StoreId).IsRequired().HasMaxLength(50);
            entity.Property(o => o.RegisterId).IsRequired().HasMaxLength(50);
            entity.Property(o => o.Payload).IsRequired();
            entity.Property(o => o.LastError).HasMaxLength(500);

            // SQLite returns DateTime without a Kind; everything here is stored as UTC.
            entity.Property(o => o.OccurredAtUtc)
                  .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            // A resend can never create a second copy; the sync worker finds unsent rows fast.
            entity.HasIndex(o => o.EventId).IsUnique();
            entity.HasIndex(o => new { o.SentAtUtc, o.RejectedAtUtc });
        });

        modelBuilder.Entity<ProductVariant>(entity =>
        {
            entity.HasOne(v => v.Product)
                  .WithMany()
                  .HasForeignKey(v => v.ProductId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.Property(v => v.Name).HasMaxLength(50);
            entity.Ignore(v => v.DisplayName);
            entity.HasIndex(v => v.Barcode);
            entity.HasIndex(v => v.Plu);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasOne<Supplier>()
                  .WithMany()
                  .HasForeignKey(p => p.SupplierId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ProductBarcode>(entity =>
        {
            entity.Property(b => b.Code).IsRequired().HasMaxLength(50);
            entity.Property(b => b.LookupKey).IsRequired().HasMaxLength(50);

            // One barcode can only ever mean one thing.
            entity.HasIndex(b => b.LookupKey).IsUnique();

            entity.HasOne(b => b.Product).WithMany().HasForeignKey(b => b.ProductId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(b => b.Variant).WithMany().HasForeignKey(b => b.VariantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StockMovement>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).ValueGeneratedOnAdd();
            entity.Property(m => m.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(m => m.Quantity).HasPrecision(18, 3);
            entity.Property(m => m.UnitCost).HasPrecision(18, 4);
            entity.Property(m => m.Reference).HasMaxLength(100);
            entity.Property(m => m.EmployeeName).HasMaxLength(200);

            // SQLite returns DateTime without a Kind; everything here is stored as UTC.
            entity.Property(m => m.AtUtc)
                  .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            // History is kept even if a product is deleted by hand in the database: no cascade.
            entity.HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(m => new { m.ProductId, m.AtUtc });
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(s => s.Name).IsRequired().HasMaxLength(200);
            entity.Property(s => s.LegacyNumber).HasMaxLength(50);
            entity.HasIndex(s => s.Name);
        });

        modelBuilder.Entity<ProductSupplierCode>(entity =>
        {
            entity.Property(c => c.Code).IsRequired().HasMaxLength(50);
            entity.HasIndex(c => c.Code);
            entity.HasOne<Product>().WithMany().HasForeignKey(c => c.ProductId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Supplier>().WithMany().HasForeignKey(c => c.SupplierId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
