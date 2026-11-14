using Microsoft.EntityFrameworkCore;

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
