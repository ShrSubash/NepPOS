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
    public DbSet<SupplierSkippedItem> SupplierSkippedItems => Set<SupplierSkippedItem>();
    public DbSet<StockReceipt> StockReceipts => Set<StockReceipt>();
    public DbSet<StockReceiptLine> StockReceiptLines => Set<StockReceiptLine>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<GiftCard> GiftCards => Set<GiftCard>();
    public DbSet<GiftCardMovement> GiftCardMovements => Set<GiftCardMovement>();
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
            entity.Ignore(st => st.HasReturns);
            entity.Ignore(st => st.IsRefund);
            entity.HasIndex(st => st.ReturnOfSaleId);

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
            entity.Property(p => p.Reference).HasMaxLength(50);
            entity.Property(p => p.TenderName).HasMaxLength(50);
            entity.Ignore(p => p.IsRefund);
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
            entity.Ignore(li => li.IsReturn);
            entity.Property(li => li.ReturnReason).HasMaxLength(50);
            entity.Property(li => li.GiftCardCode).HasMaxLength(20);
            entity.HasIndex(li => li.ReturnOfLineId);
        });

        modelBuilder.Entity<GiftCard>(entity =>
        {
            entity.HasKey(g => g.Id);
            entity.Property(g => g.Code).IsRequired().HasMaxLength(20);
            entity.HasIndex(g => g.Code).IsUnique();
            entity.Property(g => g.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(g => g.Balance).HasPrecision(18, 2);
            entity.Ignore(g => g.KindName);
            entity.Property(g => g.IssuedUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            entity.Property(g => g.LastUsedUtc).HasConversion(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : null);
            entity.HasMany(g => g.Movements).WithOne().HasForeignKey(m => m.GiftCardId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GiftCardMovement>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).ValueGeneratedOnAdd();
            entity.Property(m => m.Amount).HasPrecision(18, 2);
            entity.Property(m => m.Note).IsRequired().HasMaxLength(200);
            entity.Property(m => m.AtUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
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
            entity.Property(s => s.AccountNumber).HasMaxLength(50);
            entity.HasIndex(s => s.Name);
        });

        modelBuilder.Entity<StockReceipt>(entity =>
        {
            entity.Property(r => r.SupplierName).IsRequired().HasMaxLength(200);
            entity.Property(r => r.InvoiceNumber).IsRequired().HasMaxLength(50);
            entity.Property(r => r.EmployeeName).HasMaxLength(200);
            entity.Property(r => r.PostedUtc)
                  .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            entity.Property(r => r.ReversedBy).HasMaxLength(200);
            entity.Property(r => r.ReversalReason).HasMaxLength(200);
            entity.Property(r => r.ReversedUtc)
                  .HasConversion(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : null);
            entity.Ignore(r => r.IsReversed);
            // The database itself refuses the same invoice twice from one supplier, unless the first was reversed.
            entity.HasIndex(r => new { r.SupplierId, r.InvoiceNumber }).IsUnique().HasFilter("[ReversedUtc] IS NULL");
            entity.HasOne<Supplier>().WithMany().HasForeignKey(r => r.SupplierId).OnDelete(DeleteBehavior.SetNull);
            entity.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.StockReceiptId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StockReceiptLine>(entity =>
        {
            entity.Property(l => l.ProductName).IsRequired().HasMaxLength(200);
            entity.Property(l => l.PackName).IsRequired().HasMaxLength(50);
            entity.Property(l => l.Quantity).HasPrecision(18, 3);
            entity.Property(l => l.CostPerPack).HasPrecision(18, 4);
            entity.Property(l => l.PreviousUnitCost).HasPrecision(18, 4);
            entity.Property(l => l.PreviousLastCost).HasPrecision(18, 4);
            entity.Property(l => l.PreviousAverageCost).HasPrecision(18, 4);
            entity.Ignore(l => l.Singles);
            entity.Ignore(l => l.UnitCost);
            entity.Ignore(l => l.LineCost);
            // History stays even if a product is deleted: no foreign key to Products.
            entity.HasIndex(l => l.ProductId);
        });

        modelBuilder.Entity<ProductSupplierCode>(entity =>
        {
            entity.Property(c => c.Code).IsRequired().HasMaxLength(50);
            entity.HasIndex(c => c.Code);
            entity.Property(c => c.SupplierUnitsPerSingle).HasDefaultValue(1);
            entity.HasOne<Product>().WithMany().HasForeignKey(c => c.ProductId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Supplier>().WithMany().HasForeignKey(c => c.SupplierId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SupplierSkippedItem>(entity =>
        {
            entity.Property(i => i.Code).IsRequired().HasMaxLength(50);
            entity.Property(i => i.Description).IsRequired().HasMaxLength(200);
            entity.HasIndex(i => new { i.SupplierId, i.Code }).IsUnique();
            entity.HasOne<Supplier>().WithMany().HasForeignKey(i => i.SupplierId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
