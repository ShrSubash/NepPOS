namespace NepPOS.Domain.Enums;

/// <summary>
/// Maps to legacy scancode suffixes: P1 (single), P4 (6-pack),
/// P3 (full case/carton), P2 (split case). Split2 covers a
/// secondary split size seen in frmBar2 (bottleshop-only).
/// </summary>
public enum PackType
{
    Single = 0,
    Pack = 1,
    SplitCase = 2,
    SplitCase2 = 3,
    FullCase = 4
}