namespace NepPOS.Domain.Entities;

/// <summary>
/// A single clock-in/clock-out period for an employee at a given register.
/// Duration and productivity analytics are derived from this table.
/// </summary>
public sealed class Shift
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public DateTimeOffset ClockInUtc { get; set; }
    public DateTimeOffset? ClockOutUtc { get; set; }

    public string RegisterId { get; set; } = string.Empty; // ties to the specific terminal

    public bool IsOpen => ClockOutUtc is null;
}