namespace NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

/// <summary>
/// Represents a staff member authorized to operate the register.
/// PIN is stored as a salted hash — never plaintext.
/// </summary>
public sealed class Employee
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string PinHash { get; set; } = string.Empty;
    public string PinSalt { get; set; } = string.Empty;
    public EmployeeRole Role { get; set; }
    public bool IsActive { get; set; } = true;

    // Contact and payroll details (Manage → Staff & PINs). All optional.
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public DateOnly? StartDate { get; set; }

    /// <summary>Pay per hour, used only to estimate wages on Timesheets.</summary>
    public decimal? HourlyRate { get; set; }
}