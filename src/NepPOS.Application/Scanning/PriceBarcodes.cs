namespace NepPOS.Application.Scanning;

/// <summary>
/// Price-embedded barcodes (Manage → Till options): the labels a deli or meat counter's label scale prints,
/// EAN-13 starting with 2 (GS1 keeps 20–29 for in-store use, so no manufacturer's barcode starts with them).
/// Layout: 2 digits prefix · item code · price in cents · check digit, e.g. 21 00123 00495 c = PLU 123 at $4.95.
/// NepPOS only reads the price the label already shows; it doesn't weigh anything.
/// </summary>
public sealed record PriceBarcodeSettings
{
    public bool Enabled { get; init; }

    /// <summary>The first two digits that mark a price label, e.g. "20" to "29".</summary>
    public string FirstPrefix { get; init; } = "20";
    public string LastPrefix { get; init; } = "29";

    /// <summary>How many digits after the prefix are the item code (matched to a product's PLU). The rest, up to the check digit, is the price in cents.</summary>
    public int ItemDigits { get; init; } = 5;
}

/// <summary>A product and the price printed on its label.</summary>
public sealed record PricedItem(Domain.Entities.Product Product, decimal Price, string Barcode);

public static class PriceBarcodes
{
    public const int Length = 13;

    /// <summary>
    /// Reads a price label. Returns false for anything that isn't one (wrong length or prefix, bad check digit,
    /// switched off), so the code is then looked up like any other barcode.
    /// </summary>
    public static bool TryRead(string code, PriceBarcodeSettings settings, out string itemCode, out decimal price)
    {
        ArgumentNullException.ThrowIfNull(settings);
        itemCode = "";
        price = 0;

        if (!settings.Enabled || code is null || code.Length != Length || !code.All(char.IsAsciiDigit))
            return false;
        if (string.CompareOrdinal(code[..2], settings.FirstPrefix) < 0 || string.CompareOrdinal(code[..2], settings.LastPrefix) > 0)
            return false;
        if (settings.ItemDigits is < 3 or > 7 || !HasValidCheckDigit(code))
            return false;

        var priceDigits = Length - 3 - settings.ItemDigits;   // prefix (2) and check digit (1) aside
        itemCode = code.Substring(2, settings.ItemDigits).TrimStart('0');
        price = long.Parse(code.Substring(2 + settings.ItemDigits, priceDigits)) / 100m;
        return itemCode.Length > 0;
    }

    /// <summary>The standard EAN-13 check: weights 1,3,1,3… on the first 12 digits.</summary>
    public static bool HasValidCheckDigit(string ean13)
    {
        if (ean13.Length != Length) return false;
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (ean13[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10 == ean13[12] - '0';
    }
}
