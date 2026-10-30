namespace NepPOS.Domain.Enums;

/// <summary>
/// Drives menu visibility and route access across the Register UI.
/// Ordered by increasing privilege for simple >= comparisons.
/// </summary>
public enum EmployeeRole
{
    Cashier = 0,
    ShiftSupervisor = 1,
    Manager = 2,
    Owner = 3
}