using System.Globalization;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Application.Catalog;

/// <summary>A pack the old POS sold: its price, how many singles it holds, and its barcodes.</summary>
public sealed record LegacyPack(PackType Type, int Units, decimal Price, IReadOnlyList<string> Barcodes);

/// <summary>One product from the old POS, ready to be created.</summary>
public sealed record LegacyProduct(
    int Line,
    string? Plu,
    string Name,
    decimal Price,
    decimal? Cost,
    string Department,
    bool IsGstFree,
    bool IsActive,
    bool OnWebsite,
    decimal Stock,
    IReadOnlyList<string> Barcodes,
    IReadOnlyList<LegacyPack> Packs,
    string? SupplierNumber,
    IReadOnlyList<string> SupplierCodes);

/// <summary>What the import would do. Nothing is saved until it is confirmed.</summary>
public sealed record LegacyImportPlan(
    IReadOnlyList<LegacyProduct> New,
    int AlreadyHere,
    IReadOnlyList<ProductCsvProblem> Skipped,
    IReadOnlyList<ProductCsvProblem> Notes,
    IReadOnlyDictionary<string, string> Suppliers,
    int Rows)
{
    public bool HasWork => New.Count > 0;
}

/// <summary>
/// Reads the product table of the shop's previous POS (myPOS / RSA PCPOS, dbo.plu) as saved from SQL Server
/// Management Studio with headings ("Copy with Headers", tab or comma separated), and turns each row into a
/// NepPOS product: the single price plus a pack for each priced split, 6-pack, case and second split, every
/// barcode on the pack its P-tag names, the supplier's item codes, and stock on hand as an opening movement.
/// Department and supplier names come from their own lists when given. The old running totals (month and year
/// sales, purchases, old prices) are not carried over: NepPOS works those out from sales.
/// Products whose PLU is already in NepPOS are left alone.
/// </summary>
public static class LegacyPluImport
{
    private static readonly string[] Required = { "Number", "description", "singleprice" };

    public static LegacyImportPlan Plan(string pluText, string? departmentsText, string? suppliersText,
        IReadOnlyCollection<string> existingPlus, IReadOnlyCollection<string> existingBarcodes)
    {
        var skipped = new List<ProductCsvProblem>();
        var notes = new List<ProductCsvProblem>();
        var departments = ReadNames(departmentsText, "dpn", notes, "departments");
        var suppliers = ReadNames(suppliersText, "supplier", notes, "suppliers");

        var records = ProductCsv.SplitRecords(pluText, ProductCsv.DetectDelimiter(pluText)).ToList();
        if (records.Count == 0)
            return Empty(new ProductCsvProblem(0, "The file is empty."));

        var header = records[0].Cells.Select(h => h.Trim()).ToList();
        var column = header.Select((name, index) => (name, index))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);
        var missing = Required.Where(r => !column.ContainsKey(r)).ToList();
        if (missing.Count > 0)
            return Empty(new ProductCsvProblem(1, $"This doesn't look like the old POS product table: no {string.Join(", ", missing)} column. In SQL Server Management Studio use \"Copy with Headers\"."));

        var plusTaken = new HashSet<string>(existingPlus.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()), StringComparer.OrdinalIgnoreCase);
        var barcodesTaken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in existingBarcodes.Where(c => !string.IsNullOrWhiteSpace(c)))
            barcodesTaken.TryAdd(ProductBarcode.KeyFor(code), "a product already in NepPOS");

        var created = new List<LegacyProduct>();
        var alreadyHere = 0;
        var usedSuppliers = new Dictionary<string, string>();

        foreach (var (line, cells) in records.Skip(1))
        {
            if (cells.All(string.IsNullOrWhiteSpace))
                continue;

            string Text(string name) =>
                column.TryGetValue(name, out var i) && i < cells.Count && !cells[i].Trim().Equals("NULL", StringComparison.OrdinalIgnoreCase)
                    ? cells[i].Trim()
                    : "";
            decimal Number(string name) =>
                decimal.TryParse(Text(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && Math.Abs(value) > 0.00001m
                    ? value
                    : 0m;
            int Whole(string name) => (int)Math.Round(Number(name));

            var plu = Text("Number");
            var name = Text("longdescrip") is { Length: > 0 } longName ? longName : Text("description");
            if (name.Length == 0)
            {
                skipped.Add(new(line, $"PLU {plu}: no description."));
                continue;
            }
            if (plu.Length > 0 && !plusTaken.Add(plu))
            {
                // Already in NepPOS (or twice in this file): never overwrite what's there.
                if (existingPlus.Contains(plu, StringComparer.OrdinalIgnoreCase)) alreadyHere++;
                else skipped.Add(new(line, $"PLU {plu} ({name}) is in the file twice; only the first is used."));
                continue;
            }
            var label = plu.Length > 0 ? $"PLU {plu} ({name})" : name;

            // ---- barcodes, grouped by the pack their tag names ----
            var tagged = new Dictionary<string, List<string>>();
            for (var i = 1; i <= 16; i++)
            {
                var raw = Text($"scancodes{i}");
                if (raw.Length == 0) continue;
                var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var code = parts[0];
                var tag = parts.Length > 1 && parts[^1].StartsWith('P') ? parts[^1].ToUpperInvariant() : "P1";
                tag = tag switch { "P4" => "P7", "P6" => "P9", "P5" => "P1", _ => tag };

                var key = ProductBarcode.KeyFor(code);
                if (barcodesTaken.TryGetValue(key, out var owner))
                {
                    notes.Add(new(line, $"{label}: barcode {code} left out, it already belongs to {owner}."));
                    continue;
                }
                barcodesTaken[key] = label;
                (tagged.TryGetValue(tag, out var list) ? list : tagged[tag] = new List<string>()).Add(code);
            }

            // ---- packs: only the ones with a price ----
            var packs = new List<LegacyPack>();
            void Pack(string tag, PackType type, int units, decimal price, string what)
            {
                var codes = tagged.Remove(tag, out var list) ? list : new List<string>();
                if (price > 0 && units > 1)
                    packs.Add(new LegacyPack(type, units, price, codes));
                else if (codes.Count > 0)
                    notes.Add(new(line, $"{label}: {codes.Count} {what} barcode(s) left out, the old POS had no {what} price."));
            }
            Pack("P2", PackType.SplitCase, Whole("splitsize"), Number("splitprice"), "split");
            Pack("P7", PackType.Pack, 6, Number("price4"), "6-pack");
            Pack("P3", PackType.FullCase, Whole("packsize"), Number("packprice"), "case");
            Pack("P9", PackType.SplitCase2, Whole("split2size"), Number("split2price"), "second split");

            var singles = tagged.Remove("P1", out var p1) ? p1 : new List<string>();
            foreach (var (tag, codes) in tagged)
                notes.Add(new(line, $"{label}: {codes.Count} barcode(s) tagged {tag} left out (unknown pack)."));

            // ---- the rest ----
            var cost = Number("averagecost") is > 0 and var average ? average : Number("singlecost") is > 0 and var single ? single : (decimal?)null;
            var deptNumber = Text("dept") is { Length: > 0 } d && d != "0" ? d : Text("deptnumber");
            var department = deptNumber.Length == 0 || deptNumber == "0" ? "Other"
                : departments.TryGetValue(deptNumber, out var deptName) ? deptName
                : $"Department {deptNumber}";

            var supplierNumber = Text("supplier") is { Length: > 0 } s && s != "0" ? s : null;
            if (supplierNumber is not null)
                usedSuppliers[supplierNumber] = suppliers.TryGetValue(supplierNumber, out var supplierName) ? supplierName : $"Supplier {supplierNumber}";

            var supplierCodes = Enumerable.Range(1, 10).Select(i => Text($"suppcodes{i}")).Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var price = Number("singleprice");
            if (price == 0 && packs.Count == 0)
                notes.Add(new(line, $"{label}: no price in the old POS; it comes across at $0.00."));

            created.Add(new LegacyProduct(
                line,
                plu.Length > 0 ? plu : null,
                name,
                Math.Round(price, 2),
                cost is null ? null : Math.Round(cost.Value, 4),
                department,
                IsGstFree: Text("taxcode").Equals("A", StringComparison.OrdinalIgnoreCase),
                IsActive: Text("Status") != "9",
                OnWebsite: Text("onlineshopenabled") == "1",
                Stock: Math.Round(Number("SOH"), 3),
                singles,
                packs,
                supplierNumber,
                supplierCodes));
        }

        return new LegacyImportPlan(created, alreadyHere, skipped, notes, usedSuppliers, records.Count - 1);

        static LegacyImportPlan Empty(ProductCsvProblem problem) =>
            new(Array.Empty<LegacyProduct>(), 0, new[] { problem }, Array.Empty<ProductCsvProblem>(), new Dictionary<string, string>(), 0);
    }

    /// <summary>Number → name from a departments (Number, dpn) or suppliers (Number, supplier) list. Optional.</summary>
    private static Dictionary<string, string> ReadNames(string? text, string nameColumn, List<ProductCsvProblem> notes, string what)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return names;

        var records = ProductCsv.SplitRecords(text, ProductCsv.DetectDelimiter(text)).ToList();
        var header = records[0].Cells.Select(h => h.Trim()).ToList();
        var number = header.FindIndex(h => h.Equals("Number", StringComparison.OrdinalIgnoreCase));
        var name = header.FindIndex(h => h.Equals(nameColumn, StringComparison.OrdinalIgnoreCase));
        if (number < 0 || name < 0)
        {
            notes.Add(new(0, $"The {what} list needs Number and {nameColumn} headings; names weren't used."));
            return names;
        }

        foreach (var (_, cells) in records.Skip(1))
        {
            if (cells.Count <= Math.Max(number, name)) continue;
            var key = cells[number].Trim();
            var value = cells[name].Trim();
            if (key.Length > 0 && value.Length > 0)
                names.TryAdd(key, value);
        }
        return names;
    }
}
