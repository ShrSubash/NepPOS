using System.Text.RegularExpressions;

namespace NepPOS.Application.Sales;

/// <summary>
/// The code in the barcode at the bottom of every receipt: "RT" + the register's number + "-" + the invoice
/// number, e.g. "RT001-19" for REG-001 invoice 19. Letters first, so it can never be mistaken for a PLU or
/// product barcode (digits only) or a gift card (GC-…). Code 128, which every ordinary scanner reads.
/// </summary>
public static partial class ReceiptCode
{
    public static string For(string registerId, long invoiceNumber) => $"RT{RegisterPart(registerId)}-{invoiceNumber}";

    /// <summary>Reads a scanned or typed code. Lower case and spaces are fine.</summary>
    public static bool TryParse(string? code, out string registerPart, out long invoiceNumber)
    {
        registerPart = "";
        invoiceNumber = 0;
        var match = Pattern().Match((code ?? "").Trim());
        if (!match.Success || !long.TryParse(match.Groups["invoice"].Value, out invoiceNumber) || invoiceNumber <= 0)
            return false;
        registerPart = match.Groups["register"].Value.ToUpperInvariant();
        return true;
    }

    public static bool Looks(string? code) => Pattern().IsMatch((code ?? "").Trim());

    /// <summary>"REG-001" → "001"; a register code without digits is used as it is (letters only).</summary>
    public static string RegisterPart(string registerId)
    {
        var digits = new string((registerId ?? "").Where(char.IsDigit).ToArray());
        return digits.Length > 0 ? digits : new string((registerId ?? "").Where(char.IsLetter).ToArray()).ToUpperInvariant();
    }

    [GeneratedRegex(@"^RT(?<register>[0-9A-Za-z]+)-(?<invoice>\d{1,12})$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
