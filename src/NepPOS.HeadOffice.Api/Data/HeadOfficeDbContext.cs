using Microsoft.EntityFrameworkCore;
using NepPOS.Contracts.Licensing;

namespace NepPOS.HeadOffice.Api.Data;

/// <summary>
/// Head Office data. Every row belongs to one tenant (a business that bought NepPOS), and every
/// query a register makes is limited to its own tenant and store by the API key it signed in with,
/// never by anything it sends (docs/adr/0001).
/// </summary>
public sealed class HeadOfficeDbContext : DbContext
{
    public HeadOfficeDbContext(DbContextOptions<HeadOfficeDbContext> options) : base(options)
    {
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Register> Registers => Set<Register>();
    public DbSet<ActivationCode> ActivationCodes => Set<ActivationCode>();
    public DbSet<ReceivedEvent> ReceivedEvents => Set<ReceivedEvent>();
    public DbSet<WebsiteKey> WebsiteKeys => Set<WebsiteKey>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<WebsiteEvent> WebsiteEvents => Set<WebsiteEvent>();
    public DbSet<StockLevelRow> StockLevels => Set<StockLevelRow>();
    public DbSet<SharedCustomer> SharedCustomers => Set<SharedCustomer>();
    public DbSet<SharedAccountLine> SharedAccountLines => Set<SharedAccountLine>();
    public DbSet<SharedGiftCard> SharedGiftCards => Set<SharedGiftCard>();
    public DbSet<SharedGiftCardLine> SharedGiftCardLines => Set<SharedGiftCardLine>();
    public DbSet<PlatformUser> PlatformUsers => Set<PlatformUser>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<TenantLicence> TenantLicences => Set<TenantLicence>();
    public DbSet<LicenceSigningKey> LicenceSigningKeys => Set<LicenceSigningKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<Store>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.StoreCode).IsRequired().HasMaxLength(50);
            entity.Property(s => s.Name).IsRequired().HasMaxLength(200);
            entity.Property(s => s.TimeZoneId).IsRequired().HasMaxLength(64);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);

            // "STORE-001" can exist in two businesses, but only once in each.
            entity.HasIndex(s => new { s.TenantId, s.StoreCode }).IsUnique();
        });

        modelBuilder.Entity<Register>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.RegisterCode).IsRequired().HasMaxLength(50);
            entity.Property(r => r.DeviceName).HasMaxLength(100);
            entity.Property(r => r.ApiKeyHash).IsRequired().HasMaxLength(64);
            entity.Property(r => r.ApiKeyPrefix).IsRequired().HasMaxLength(12);
            entity.HasOne<Store>().WithMany().HasForeignKey(r => r.StoreId).OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(r => r.ApiKeyHash).IsUnique();
            entity.HasIndex(r => new { r.StoreId, r.RegisterCode });
        });

        modelBuilder.Entity<ActivationCode>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.CodeHash).IsRequired().HasMaxLength(64);
            entity.HasOne<Store>().WithMany().HasForeignKey(a => a.StoreId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(a => a.CodeHash).IsUnique();
        });

        modelBuilder.Entity<WebsiteKey>(entity =>
        {
            entity.HasKey(k => k.Id);
            entity.Property(k => k.Name).IsRequired().HasMaxLength(100);
            entity.Property(k => k.ApiKeyHash).IsRequired().HasMaxLength(64);
            entity.Property(k => k.ApiKeyPrefix).IsRequired().HasMaxLength(12);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(k => k.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(k => k.ApiKeyHash).IsUnique();
        });

        modelBuilder.Entity<ReceivedEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Payload).IsRequired();
            entity.HasOne<Register>().WithMany().HasForeignKey(e => e.RegisterId).OnDelete(DeleteBehavior.Restrict);

            // The two rules that make resending safe and gaps visible.
            entity.HasIndex(e => e.EventId).IsUnique();
            entity.HasIndex(e => new { e.RegisterId, e.Sequence }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.StoreId, e.OccurredAtUtc });
        });

        modelBuilder.Entity<StockLevelRow>(entity =>
        {
            entity.HasKey(s => new { s.StoreId, s.ProductId });
            entity.Property(s => s.StockOnHand).HasPrecision(18, 3);
            entity.HasIndex(s => new { s.StoreId, s.LastEventId });
            entity.HasOne<Store>().WithMany().HasForeignKey(s => s.StoreId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WebsiteEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Payload).IsRequired();
            entity.HasIndex(e => e.EventId).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.Id });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WebsiteKey>().WithMany().HasForeignKey(e => e.WebsiteKeyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SharedCustomer>(entity =>
        {
            entity.HasKey(c => new { c.TenantId, c.CustomerId });
            entity.Property(c => c.Code).IsRequired().HasMaxLength(20);
            entity.Property(c => c.Name).IsRequired().HasMaxLength(200);
            entity.Property(c => c.Email).HasMaxLength(200);
            entity.Property(c => c.Phone).HasMaxLength(30);
            entity.Property(c => c.CreditLimit).HasPrecision(18, 2);
            entity.Property(c => c.Balance).HasPrecision(18, 2);
            entity.HasIndex(c => new { c.TenantId, c.Email });
            entity.HasIndex(c => new { c.TenantId, c.Code });
        });

        modelBuilder.Entity<SharedAccountLine>(entity =>
        {
            entity.HasKey(l => l.EntryId);
            entity.Property(l => l.Kind).IsRequired().HasMaxLength(20);
            entity.Property(l => l.Amount).HasPrecision(18, 2);
            entity.Property(l => l.Where).HasMaxLength(100);
            entity.Property(l => l.Note).HasMaxLength(300);
            entity.HasIndex(l => new { l.TenantId, l.CustomerId });
        });

        modelBuilder.Entity<SharedGiftCard>(entity =>
        {
            entity.HasKey(g => new { g.TenantId, g.Code });
            entity.Property(g => g.Code).HasMaxLength(20);
            entity.Property(g => g.Kind).IsRequired().HasMaxLength(20);
            entity.Property(g => g.Balance).HasPrecision(18, 2);
        });

        modelBuilder.Entity<SharedGiftCardLine>(entity =>
        {
            entity.HasKey(l => l.EventId);
            entity.Property(l => l.Code).IsRequired().HasMaxLength(20);
            entity.Property(l => l.Amount).HasPrecision(18, 2);
            entity.Property(l => l.Note).HasMaxLength(200);
            entity.Property(l => l.Where).HasMaxLength(100);
            entity.HasIndex(l => new { l.TenantId, l.Code });
        });

        modelBuilder.Entity<CatalogItem>(entity =>
        {
            entity.HasKey(c => new { c.StoreId, c.ProductId });
            entity.Property(c => c.Name).IsRequired().HasMaxLength(200);
            entity.Property(c => c.Category).IsRequired().HasMaxLength(100);
            entity.Property(c => c.Barcode).HasMaxLength(50);
            entity.Property(c => c.Plu).HasMaxLength(50);
            entity.Property(c => c.Price).HasPrecision(18, 2);
            entity.Property(c => c.VariantsJson).IsRequired();
            entity.HasOne<Store>().WithMany().HasForeignKey(c => c.StoreId).OnDelete(DeleteBehavior.Restrict);

            // The event that last changed it: its database id is the catalog's change number.
            entity.HasOne(c => c.LastEvent).WithMany().HasForeignKey(c => c.LastEventId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(c => new { c.StoreId, c.LastEventId });
        });

        modelBuilder.Entity<TenantLicence>(entity =>
        {
            entity.HasKey(l => l.TenantId);
            entity.Property(l => l.Plan).IsRequired().HasMaxLength(20);
            entity.Property(l => l.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(l => l.Features).IsRequired().HasMaxLength(200);
            entity.Property(l => l.Message).HasMaxLength(300);
            entity.HasOne<Tenant>().WithOne().HasForeignKey<TenantLicence>(l => l.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LicenceSigningKey>(entity =>
        {
            entity.HasKey(k => k.Id);
            entity.Property(k => k.PrivateKeyPem).IsRequired();
        });

        modelBuilder.Entity<PlatformUser>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Email).IsRequired().HasMaxLength(256);
            entity.Property(u => u.Name).IsRequired().HasMaxLength(100);
            entity.Property(u => u.Role).IsRequired().HasMaxLength(20);
            entity.Property(u => u.PasswordHash).IsRequired();
            entity.Property(u => u.TotpSecret).HasMaxLength(64);
            entity.Property(u => u.PendingTotpSecret).HasMaxLength(64);
            entity.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Actor).IsRequired().HasMaxLength(256);
            entity.Property(a => a.Action).IsRequired().HasMaxLength(50);
            entity.Property(a => a.Summary).IsRequired().HasMaxLength(500);
            entity.HasIndex(a => a.AtUtc);
            entity.HasIndex(a => new { a.TenantId, a.AtUtc });
        });

        // Every time here is UTC. SQLite forgets that when it reads a time back, which would make
        // the API answer "10:54" instead of "10:54Z"; mark them all as UTC on the way out.
        var utc = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
            v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?>(
            v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (property.ClrType == typeof(DateTime))
                property.SetValueConverter(utc);
            else if (property.ClrType == typeof(DateTime?))
                property.SetValueConverter(utcNullable);
        }
    }
}

/// <summary>A business that uses NepPOS. Everything else hangs off one of these.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One shop of a tenant. <see cref="StoreCode"/> is what its registers send (e.g. "STORE-001").</summary>
public sealed class Store
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string StoreCode { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Where the store is, for "today" and opening hours in reports (IANA id, e.g. Australia/Perth).</summary>
    public string TimeZoneId { get; set; } = "Australia/Sydney";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An activated register. Only a SHA-256 hash of its API key is kept: the key itself is shown
/// once, at activation, and can't be read back from the database.
/// </summary>
public sealed class Register
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public string RegisterCode { get; set; } = "";
    public string? DeviceName { get; set; }
    public string ApiKeyHash { get; set; } = "";

    /// <summary>The first characters of the key, so a person can tell keys apart ("npk_Ab3x…").</summary>
    public string ApiKeyPrefix { get; set; } = "";

    public DateTime ActivatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>A lost or replaced PC: its key stops working at once.</summary>
    public bool IsRevoked { get; set; }
}

/// <summary>A one-time code that activates one register for one store. Only its hash is stored.</summary>
public sealed class ActivationCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}

/// <summary>One event from a register's outbox, stored exactly once (docs/integration/events.md).</summary>
public sealed class ReceivedEvent
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid RegisterId { get; set; }
    public string Type { get; set; } = "";
    public int Version { get; set; }
    public long Sequence { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Payload { get; set; } = "";
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A key a business's website (or another sales channel) uses to read that business's reports.
/// It can't send events or see any other business. Only its hash is stored.
/// </summary>
public sealed class WebsiteKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string ApiKeyHash { get; set; } = "";
    public string ApiKeyPrefix { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedUtc { get; set; }
    public bool IsRevoked { get; set; }
}

/// <summary>A product's latest stock on hand at one store (from <c>stock.changed</c>), which the website reads.</summary>
public sealed class StockLevelRow
{
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid ProductId { get; set; }
    public decimal StockOnHand { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>The <see cref="ReceivedEvent"/> that last changed it: the website's change number.</summary>
    public long LastEventId { get; set; }
}

/// <summary>
/// A change the business's website made (a customer signed up, a gift card spent online). Kept like a register's
/// event and passed on to every register (GET /api/v1/sync/shared, numbered separately from register events).
/// </summary>
public sealed class WebsiteEvent
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid TenantId { get; set; }
    public Guid WebsiteKeyId { get; set; }
    public string Type { get; set; } = "";
    public int Version { get; set; } = 1;
    public DateTime OccurredAtUtc { get; set; }
    public string Payload { get; set; } = "";
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Head Office's copy of a customer (newest edit from any register or the website), with their account balance.</summary>
public sealed class SharedCustomer
{
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool IsAccount { get; set; }
    public decimal CreditLimit { get; set; }
    public int TermsDays { get; set; }
    public bool IsActive { get; set; } = true;
    public decimal Balance { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Head Office's copy of one line on a customer's account.</summary>
public sealed class SharedAccountLine
{
    public Guid EntryId { get; set; }
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public string Kind { get; set; } = "";
    public decimal Amount { get; set; }
    public long? InvoiceNumber { get; set; }
    public DateTime AtUtc { get; set; }
    public DateTime? DueUtc { get; set; }
    public string? Where { get; set; }
    public string? Note { get; set; }
}

/// <summary>Head Office's copy of a gift card or store credit and its balance across all stores and the website.</summary>
public sealed class SharedGiftCard
{
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Kind { get; set; } = "GiftCard";
    public decimal Balance { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastUsedUtc { get; set; }
}

/// <summary>One movement of a gift card (from a register or the website); its id is the event's id.</summary>
public sealed class SharedGiftCardLine
{
    public Guid EventId { get; set; }
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime AtUtc { get; set; }
    public string? Note { get; set; }
    public string? Where { get; set; }
}

/// <summary>
/// The latest copy of one product at one store, from the newest <c>product.changed</c> its registers sent.
/// The events themselves stay in <see cref="ReceivedEvent"/>; this is what the website reads.
/// </summary>
public sealed class CatalogItem
{
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid ProductId { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public string? Barcode { get; set; }
    public string? Plu { get; set; }
    public bool EnableOnWebsite { get; set; }
    public bool IsActive { get; set; }

    /// <summary>Pack sizes as JSON (a list of ProductVariantV1).</summary>
    public string VariantsJson { get; set; } = "[]";

    /// <summary>When the register made the change. An older event arriving late doesn't undo a newer one.</summary>
    public DateTime ChangedAtUtc { get; set; }

    /// <summary>The <see cref="ReceivedEvent"/> that last changed it. Counts up, so the website asks "after N".</summary>
    public long LastEventId { get; set; }
    public ReceivedEvent LastEvent { get; set; } = null!;
}

/// <summary>
/// Someone at the NepPOS company who signs in to NepPOS Portal (docs/portal/README.md). Not a shop's
/// staff: they never see the Portal.
/// </summary>
public sealed class PlatformUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stored in lower case; the sign-in name.</summary>
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Owner or Support (NepPOS.Contracts.Admin.StaffRoles).</summary>
    public string Role { get; set; } = "Support";

    /// <summary>ASP.NET Identity's password hash (PBKDF2, salted, slow on purpose).</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>The authenticator app's secret (Base32), once set up.</summary>
    public string? TotpSecret { get; set; }
    public bool TwoFactorEnabled { get; set; }

    /// <summary>A secret shown on the setup page, waiting for its first code.</summary>
    public string? PendingTotpSecret { get; set; }

    /// <summary>The time step of the last code accepted, so the same code can't be used twice.</summary>
    public long LastTotpStep { get; set; } = -1;

    public bool IsActive { get; set; } = true;
    public int FailedSignIns { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime? LastSignInUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One change made through the admin API: who (Portal user, or "admin key" for scripts), what,
/// to which business, when. Never edited or deleted.
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = "";

    /// <summary>A short code, e.g. "store.created", "register.revoked".</summary>
    public string Action { get; set; } = "";
    public Guid? TenantId { get; set; }

    /// <summary>In words, e.g. "Created store STORE-002 (Parramatta)". Never contains a secret.</summary>
    public string Summary { get; set; } = "";
}

/// <summary>
/// A business's NepPOS licence: its plan, limits and how long it's paid for. Set in NepPOS Portal; registers get
/// it signed with every heartbeat. A business without one gets a trial the first time it's needed.
/// </summary>
public sealed class TenantLicence
{
    public Guid TenantId { get; set; }

    /// <summary>A <see cref="LicencePlans"/> code ("standard"); the limits below can differ from the plan's.</summary>
    public string Plan { get; set; } = LicencePlans.TrialPlan.Code;

    public LicenceStatus Status { get; set; } = LicenceStatus.Trial;

    /// <summary>End of the trial or the paid period.</summary>
    public DateTime ValidUntilUtc { get; set; }

    public int MaxStores { get; set; } = 1;
    public int MaxRegisters { get; set; } = 1;

    /// <summary><see cref="LicenceFeatures"/> codes, comma separated.</summary>
    public string Features { get; set; } = "";

    /// <summary>Shown to the shop when suspended or cancelled.</summary>
    public string? Message { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public IReadOnlyList<string> FeatureList =>
        Features.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static TenantLicence NewTrial(Guid tenantId, DateTime nowUtc)
    {
        var plan = LicencePlans.TrialPlan;
        return new TenantLicence
        {
            TenantId = tenantId,
            Plan = plan.Code,
            Status = LicenceStatus.Trial,
            ValidUntilUtc = nowUtc.AddDays(LicencePlans.TrialDays),
            MaxStores = plan.MaxStores,
            MaxRegisters = plan.MaxRegisters,
            Features = string.Join(',', plan.Features),
            UpdatedUtc = nowUtc
        };
    }
}

/// <summary>
/// The licence signing key, kept in the database on developer PCs only, when HeadOffice:LicenceSigningKey isn't set.
/// In production the key comes from settings (Key Vault / App Service), never from here.
/// </summary>
public sealed class LicenceSigningKey
{
    public int Id { get; set; }
    public string PrivateKeyPem { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
