using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NepPOS.Application.Stock;

/// <summary>One line of a supplier's electronic invoice, with money already split into GST and ex-GST.</summary>
/// <param name="ItemCode">The supplier's code for what was sent.</param>
/// <param name="OrderedItemCode">The code that was ordered; different when the supplier sent a substitute.</param>
/// <param name="Barcode">The item's barcode (GTIN), if the invoice gives one.</param>
/// <param name="BarcodeMultiple">How many invoiced units that barcode covers (10 for a 10-pack's barcode).</param>
/// <param name="CasePack">Invoiced units in one of the supplier's cases.</param>
/// <param name="Units">Invoiced units (the supplier's singles). 0 when the item wasn't supplied.</param>
/// <param name="CostExGst">What the line costs before GST, including any wine tax (WET).</param>
public sealed record SupplierInvoiceLine(
    int LineNumber,
    string ItemCode,
    string? OrderedItemCode,
    string Description,
    string? Barcode,
    int BarcodeMultiple,
    int CasePack,
    decimal Units,
    decimal CostExGst,
    decimal Gst,
    bool IsSubstitute)
{
    /// <summary>Cost of one invoiced unit before GST.</summary>
    public decimal UnitCostExGst => Units == 0 ? 0 : Math.Round(CostExGst / Units, 4);
}

/// <summary>A supplier's electronic invoice, read from their file.</summary>
public sealed record SupplierInvoice(
    string SupplierName,
    string? AccountNumber,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    string? OrderNumber,
    IReadOnlyList<SupplierInvoiceLine> Lines)
{
    public decimal CostExGst => Lines.Sum(l => l.CostExGst);
    public decimal Gst => Lines.Sum(l => l.Gst);
}

/// <summary>
/// Reads supplier e-invoice files. Supported: Metcash / ALM invoices (XML, InvoiceBatch version 8).
/// Throws <see cref="InvalidDataException"/> with a message for the screen when a file can't be used.
/// </summary>
public static class SupplierInvoiceReader
{
    private const string MetcashNamespacePrefix = "http://metcash.com/invoices/";

    public static IReadOnlyList<SupplierInvoice> Read(Stream file)
    {
        ArgumentNullException.ThrowIfNull(file);

        XDocument document;
        try
        {
            // No DTDs: an invoice file never needs one, and refusing them blocks XML entity attacks.
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            throw new InvalidDataException("This file isn't an e-invoice. Choose the XML file from the supplier.");
        }

        var root = document.Root;
        if (root is null || !root.Name.NamespaceName.StartsWith(MetcashNamespacePrefix, StringComparison.OrdinalIgnoreCase) || root.Name.LocalName != "InvoiceBatch")
            throw new InvalidDataException("NepPOS can read ALM / Metcash e-invoices so far. Other suppliers' files will be added.");

        XNamespace ns = root.Name.Namespace;
        var invoices = root.Elements(ns + "Invoice").Select(i => ReadMetcashInvoice(i, ns)).ToList();
        if (invoices.Count == 0)
            throw new InvalidDataException("This e-invoice file has no invoices in it.");
        return invoices;
    }

    private static SupplierInvoice ReadMetcashInvoice(XElement invoice, XNamespace ns)
    {
        var number = ((string?)invoice.Attribute("InvoiceNumber"))?.Trim();
        var header = invoice.Element(ns + "Header");
        if (string.IsNullOrEmpty(number) || header is null)
            throw new InvalidDataException("The e-invoice is missing its invoice number. Ask the supplier for the file again.");

        if (!DateOnly.TryParseExact(Text(header, ns, "InvoiceDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException($"Invoice {number} has no readable invoice date.");

        var lines = invoice.Elements(ns + "Detail").Select(d => ReadMetcashLine(d, ns)).ToList();

        // The lines must add up to the invoice's subtotal (GST included); otherwise the file is cut short or damaged.
        var subTotal = Number(invoice.Element(ns + "Trailer"), ns, "SubTotal");
        var linesTotal = lines.Sum(l => l.CostExGst + l.Gst);
        if (subTotal is { } expected && Math.Abs(expected - linesTotal) > 0.05m)
            throw new InvalidDataException($"Invoice {number}'s lines add up to {linesTotal:C} but its subtotal says {expected:C}. The file may be incomplete. Ask the supplier to send it again.");

        var supplier = Text(header, ns, "BusinessPillar");
        return new SupplierInvoice(
            string.IsNullOrWhiteSpace(supplier) ? "Metcash" : supplier.Trim(),
            Text(header, ns, "CustomerNumber")?.Trim(),
            number,
            date,
            Text(header, ns, "OrderNumber")?.Trim(),
            lines);
    }

    private static SupplierInvoiceLine ReadMetcashLine(XElement line, XNamespace ns)
    {
        var lineNumber = (int?)line.Attribute("LineNumber") ?? 0;
        var code = Text(line, ns, "InvoicedProductCode")?.Trim() ?? "";
        var ordered = Text(line, ns, "OrderedProductCode")?.Trim();
        var units = Number(line, ns, "UnitsInvoiced") ?? 0;

        // CostPrice on these files includes GST; ExtendedCostTotal - ExtendedGSTTotal is the line's ex-GST cost.
        var total = Number(line, ns, "ExtendedCostTotal") ?? 0;
        var gst = Number(line, ns, "ExtendedGSTTotal") ?? 0;

        var gtinUnit = line.Element(ns + "GTINPLUS")?.Elements(ns + "GTINPLUUnit").FirstOrDefault();
        var barcode = gtinUnit?.Descendants(ns + "Number").Select(n => n.Value.Trim()).FirstOrDefault(n => n.Length > 0);
        var multiple = decimal.TryParse((string?)gtinUnit?.Attribute("Multiple"), NumberStyles.Number, CultureInfo.InvariantCulture, out var m) && m >= 1 ? (int)m : 1;

        var substitute = Text(line, ns, "Indicator") == "S" || (!string.IsNullOrEmpty(ordered) && ordered != code);

        return new SupplierInvoiceLine(
            lineNumber, code, ordered, CleanName(Text(line, ns, "ProductDescription") ?? code),
            barcode, multiple, (int)(Number(line, ns, "WholesalePackQuantity") ?? 1),
            units, total - gst, gst, substitute);
    }

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex GluedSize = new(@"(?<=[A-Za-z])(?=\d+(\.\d+)?(ML|L|GM|G|KG|PK)\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Size = new(@"\b(\d+(?:\.\d+)?)(ML|L|GM|G|KG|PK)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"WOODFORD RES DOUBLE OAKED700ML" → "Woodford Res Double Oaked 700ml": a readable start for a new product's name.</summary>
    public static string CleanName(string description)
    {
        var text = Spaces.Replace(description.Trim(), " ");
        text = GluedSize.Replace(text, " ");
        text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
        return Size.Replace(text, m => m.Groups[1].Value + (m.Groups[2].Value.ToLowerInvariant() == "gm" ? "g" : m.Groups[2].Value.ToLowerInvariant()));
    }

    private static string? Text(XElement? parent, XNamespace ns, string name) => parent?.Element(ns + name)?.Value;

    private static decimal? Number(XElement? parent, XNamespace ns, string name) =>
        decimal.TryParse(Text(parent, ns, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
}
