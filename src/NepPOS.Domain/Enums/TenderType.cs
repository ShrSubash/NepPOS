namespace NepPOS.Domain.Enums;

/// <summary>
/// How a sale (or part of a split sale) was paid.
/// Stored as text, so new tender types can be added without renumbering.
/// </summary>
public enum TenderType
{
    Cash = 1,
    Eftpos = 2
}
