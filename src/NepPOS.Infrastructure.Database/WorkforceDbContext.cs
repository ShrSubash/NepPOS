using Microsoft.EntityFrameworkCore;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database;

/// <summary>
/// Bounded context for workforce data (employees, shifts, clock events).
/// Shares the same physical SQLite file as NepPosDbContext via an
/// identical connection string — this is a logical separation only,
/// keeping the Master/Cloud sync pipeline single and simple.
/// </summary>
public sealed class WorkforceDbContext : DbContext
{
    public WorkforceDbContext(DbContextOptions<WorkforceDbContext> options) : base(options) { }

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Shift> Shifts => Set<Shift>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Employee>(entity =>
        {
            entity.HasIndex(e => e.PinHash);
            entity.Property(e => e.FullName).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<Shift>(entity =>
        {
            entity.HasOne(s => s.Employee)
                  .WithMany()
                  .HasForeignKey(s => s.EmployeeId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(s => new { s.EmployeeId, s.ClockInUtc });
        });
    }
}