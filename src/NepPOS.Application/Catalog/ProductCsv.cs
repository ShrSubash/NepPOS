using System.Globalization;
using System.Text;
using NepPOS.Domain.Entities;

namespace NepPOS.Application.Catalog;

/// <summary>One line of a product CSV, as read. Null = the cell was empty or the column isn't there: keep what's saved.</summary>
public sealed record ProductCsvRow(
    int Line,
    string? Plu,
    string? Name,
    decimal? Price,
    string? Category,
    string? Barcode,
    decimal? CostPrice,
    decimal? StockOnHand,
    bool? EnableOnWebsite,
    bool? IsActive);

/// <summary>Why a line wasn't used. Line 0 = the whole file.</summary>
public sealed record ProductCsvProblem(int Line, string Message);

/// <summary>An existing product with the values the file gives it, and what changes, in words.</summary>
public sealed record ProductCsvUpdate(Product Updated, IReadOnlyList<string> Changes);

/// <summary>What an import would do. Nothing is saved until it is confirmed.</summary>
public sealed record ProductImportPlan(
    IReadOnlyList<Product> New,
    IReadOnlyList<ProductCsvUpdate> Updates,
    int Unchanged,
    IReadOnlyList<ProductCsvProblem> Problems,
    int Rows)
{
    public bool HasWork => New.Count + Updates.Count > 0;
}

/// <summary>
/// Products to and from CSV, so a shop can load its PLUs from Excel or its old POS instead of typing them.
/// Columns are found by their heading in any order (PLU, Name, Price, Department, Barcode, Cost, Stock,
/// Website, Active, plus common other names); an empty cell keeps what is already saved. Rows match a saved
/// product by PLU, then barcode, then (only when the row has neither) by name.
/// </summary>
public static class ProductCsv
{
    public const int MaxRows = 20_000;

    /// <summary>The headings this register writes, in order. Export output imports back unchanged.</summary>
    public static readonly string[] Headings =
        { "PLU", "Name", "Price", "Department", "Barcode", "Cost", "Stock", "Website", "Active" };

    private enum Column { Plu, Name, Price, Category, Barcode, Cost, Stock, Website, Active }

    // Headings are compared lower-case with spaces and punctuation removed ("Sell Price" → "sellprice").
    private static readonly Dictionary<string, Column> Aliases = new()
    {
        ["plu"] = Column.Plu, ["plucode"] = Column.Plu, ["code"] = Column.Plu, ["itemcode"] = Column.Plu, ["sku"] = Column.Plu, ["productcode"] = Column.Plu,
        ["name"] = Column.Name, ["description"] = Column.Name, ["desc"] = Column.Name, ["product"] = Column.Name, ["productname"] = Column.Name, ["item"] = Column.Name, ["itemname"] = Column.Name,
        ["price"] = Column.Price, ["sellprice"] = Column.Price, ["sell"] = Column.Price, ["retail"] = Column.Price, ["retailprice"] = Column.Price, ["unitprice"] = Column.Price, ["saleprice"] = Column.Price,
        ["department"] = Column.Category, ["dept"] = Column.Category, ["category"] = Column.Category, ["group"] = Column.Category,
        ["barcode"] = Column.Barcode, ["apn"] = Column.Barcode, ["ean"] = Column.Barcode, ["upc"] = Column.Barcode,
        ["cost"] = Column.Cost, ["costprice"] = Column.Cost, ["buyprice"] = Column.Cost,
        ["stock"] = Column.Stock, ["stockonhand"] = Column.Stock, ["soh"] = Column.Stock, ["onhand"] = Column.Stock, ["qty"] = Column.Stock, ["quantity"] = Column.Stock,
        ["website"] = Column.Website, ["enableonwebsite"] = Column.Website, ["online"] = Column.Website, ["showonwebsite"] = Column.Website,
        ["active"] = Column.Active, ["isactive"] = Column.Active, ["enabled"] = Column.Active
    };

    // ---- reading ----

    /// <summary>Text from a file's bytes: UTF-8 (with or without BOM), else Windows/Excel "ANSI".</summary>
    public static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Reads the rows. Problems are lines that can't be read; the rest are returned.</summary>
    public static (List<ProductCsvRow> Rows, List<ProductCsvProblem> Problems) Read(string text)
    {
        var rows = new List<ProductCsvRow>();
        var problems = new List<ProductCsvProblem>();
        var lines = SplitRecords(text, DetectDelimiter(text)).ToList();
        if (lines.Count == 0)
        {
            problems.Add(new(0, "The file is empty."));
            return (rows, problems);
        }

        var (headerLine, header) = lines[0];
        var columns = new Dictionary<Column, int>();
        for (var i = 0; i < header.Count; i++)
        {
            if (Aliases.TryGetValue(Normalise(header[i]), out var column) && !columns.ContainsKey(column))
                columns[column] = i;
        }

        if (!columns.ContainsKey(Column.Plu) && !columns.ContainsKey(Column.Barcode) && !columns.ContainsKey(Column.Name))
        {
            problems.Add(new(headerLine, "The first line needs column headings, with at least PLU, Barcode or Name. Export to CSV shows the layout."));
            return (rows, problems);
        }

        foreach (var (lineNumber, cells) in lines.Skip(1))
        {
            if (cells.All(string.IsNullOrWhiteSpace))
                continue;
            if (rows.Count >= MaxRows)
            {
                problems.Add(new(lineNumber, $"Only the first {MaxRows:N0} products are read. Split the file."));
                break;
            }

            string? Cell(Column column)
            {
                if (!columns.TryGetValue(column, out var index) || index >= cells.Count || string.IsNullOrWhiteSpace(cells[index]))
                    return null;
                var value = cells[index].Trim();
                // Undo the ' that Export puts before a leading = + - @ (so Excel doesn't run it as a formula).
                return value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' ? value[1..] : value;
            }

            var lineProblems = new List<string>();
            var price = Money(Cell(Column.Price), "Price", lineProblems);
            var cost = Money(Cell(Column.Cost), "Cost", lineProblems);
            var stock = Number(Cell(Column.Stock), "Stock", lineProblems);
            var website = YesNo(Cell(Column.Website), "Website", lineProblems);
            var active = YesNo(Cell(Column.Active), "Active", lineProblems);

            var barcode = Cell(Column.Barcode);
            if (barcode is not null && barcode.Contains("E+", StringComparison.OrdinalIgnoreCase))
                lineProblems.Add($"Barcode {barcode} was changed by Excel. Format the Barcode column as Text and save again.");

            var name = Cell(Column.Name);
            if (name is { Length: > 200 })
                lineProblems.Add("Name is longer than 200 characters.");

            if (lineProblems.Count > 0)
            {
                problems.Add(new(lineNumber, string.Join(" ", lineProblems)));
                continue;
            }

            rows.Add(new ProductCsvRow(lineNumber, Cell(Column.Plu), name, price, Cell(Column.Category), barcode, cost, stock, website, active));
        }

        return (rows, problems);
    }

    // ---- what an import would do ----

    /// <summary>Matches each row to a saved product (or a new one) and works out what changes.</summary>
    public static ProductImportPlan Plan(string text, IReadOnlyList<Product> saved)
    {
        var (rows, problems) = Read(text);

        var byPlu = Index(saved, p => p.Plu);
        var byBarcode = Index(saved, p => p.Barcode);
        var byName = saved.GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var created = new List<Product>();
        var updates = new List<ProductCsvUpdate>();
        var unchanged = 0;
        var seenPlu = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBarcode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var touched = new HashSet<int>();

        foreach (var row in rows)
        {
            if (row.Plu is { } plu && !seenPlu.Add(plu))
            {
                problems.Add(new(row.Line, $"PLU {plu} is in the file twice; only the first is used."));
                continue;
            }
            if (row.Barcode is { } code && !seenBarcode.Add(code))
            {
                problems.Add(new(row.Line, $"Barcode {code} is in the file twice; only the first is used."));
                continue;
            }

            var match = row.Plu is { } p1 && byPlu.TryGetValue(p1, out var a) ? a
                : row.Barcode is { } b1 && byBarcode.TryGetValue(b1, out var b) ? b
                : row.Plu is null && row.Barcode is null && row.Name is { } n1 && byName.TryGetValue(n1, out var named) && named.Count == 1 ? named[0]
                : null;

            // A new PLU with another product's barcode: probably a mistake, never quietly renumber that product.
            if (match is not null && row.Plu is { } rowPlu && !string.IsNullOrWhiteSpace(match.Plu)
                && !string.Equals(match.Plu.Trim(), rowPlu, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new(row.Line, $"Barcode {row.Barcode} belongs to {match.Name} (PLU {match.Plu}), not PLU {rowPlu}."));
                continue;
            }

            if (match is null)
            {
                if (row.Name is null || row.Price is null)
                {
                    var missing = row.Name is null && row.Price is null ? "a name and a price" : row.Name is null ? "a name" : "a price";
                    problems.Add(new(row.Line, $"{Describe(row)} is new, so it needs {missing}."));
                    continue;
                }
                if (row.Barcode is { } newCode && byBarcode.ContainsKey(newCode))
                {
                    problems.Add(new(row.Line, $"Barcode {newCode} already belongs to {byBarcode[newCode].Name}."));
                    continue;
                }

                created.Add(new Product
                {
                    Plu = row.Plu,
                    Name = row.Name,
                    Price = row.Price.Value,
                    Category = row.Category ?? "",
                    Barcode = row.Barcode,
                    CostPrice = row.CostPrice,
                    StockOnHand = row.StockOnHand ?? 0,
                    EnableOnWebsite = row.EnableOnWebsite ?? false,
                    IsActive = row.IsActive ?? true
                });
                continue;
            }

            if (!touched.Add(match.Id))
            {
                problems.Add(new(row.Line, $"{Describe(row)} is the same product as an earlier line ({match.Name}); only the first is used."));
                continue;
            }
            if (row.Barcode is { } changedCode && byBarcode.TryGetValue(changedCode, out var owner) && owner.Id != match.Id)
            {
                problems.Add(new(row.Line, $"Barcode {changedCode} already belongs to {owner.Name}."));
                continue;
            }

            var (updated, changes) = Apply(match, row);
            if (changes.Count == 0)
                unchanged++;
            else
                updates.Add(new ProductCsvUpdate(updated, changes));
        }

        return new ProductImportPlan(created, updates, unchanged, problems.OrderBy(p => p.Line).ToList(), rows.Count);
    }

    /// <summary>A copy of the saved product with the row's values, and the changes in words ("Price $3.20 → $3.50").</summary>
    private static (Product Updated, List<string> Changes) Apply(Product saved, ProductCsvRow row)
    {
        var changes = new List<string>();
        var copy = new Product
        {
            Id = saved.Id, SyncId = saved.SyncId, Name = saved.Name, Plu = saved.Plu, Barcode = saved.Barcode,
            Category = saved.Category, Price = saved.Price, CostPrice = saved.CostPrice, ImageUrl = saved.ImageUrl,
            StockOnHand = saved.StockOnHand, ProductUrl = saved.ProductUrl, PointsPerUnit = saved.PointsPerUnit,
            EnableOnWebsite = saved.EnableOnWebsite, IsActive = saved.IsActive
        };

        void Text(string label, string? value, string? current, Action<string> set)
        {
            if (value is not null && !string.Equals(value, current ?? "", StringComparison.Ordinal))
            {
                changes.Add($"{label} “{current}” → “{value}”");
                set(value);
            }
        }

        Text("Name", row.Name, saved.Name, v => copy.Name = v);
        Text("PLU", row.Plu, saved.Plu, v => copy.Plu = v);
        Text("Barcode", row.Barcode, saved.Barcode, v => copy.Barcode = v);
        Text("Department", row.Category, saved.Category, v => copy.Category = v);

        if (row.Price is { } price && price != saved.Price) { changes.Add($"Price {saved.Price:0.00} → {price:0.00}"); copy.Price = price; }
        if (row.CostPrice is { } cost && cost != saved.CostPrice) { changes.Add($"Cost {saved.CostPrice:0.00} → {cost:0.00}"); copy.CostPrice = cost; }
        if (row.StockOnHand is { } stock && stock != saved.StockOnHand) { changes.Add($"Stock {saved.StockOnHand:0.##} → {stock:0.##}"); copy.StockOnHand = stock; }
        if (row.EnableOnWebsite is { } web && web != saved.EnableOnWebsite) { changes.Add(web ? "Shown on website" : "Hidden from website"); copy.EnableOnWebsite = web; }
        if (row.IsActive is { } active && active != saved.IsActive) { changes.Add(active ? "Made active" : "Made inactive"); copy.IsActive = active; }

        return (copy, changes);
    }

    // ---- writing ----

    /// <summary>All products as CSV (UTF-8 with BOM so Excel shows Nepali and other characters). Imports back unchanged.</summary>
    public static byte[] Export(IEnumerable<Product> products)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Headings));
        foreach (var p in products.OrderBy(p => p.Category).ThenBy(p => p.Name))
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Quote(p.Plu), Quote(p.Name), p.Price.ToString("0.00", CultureInfo.InvariantCulture), Quote(p.Category), Quote(p.Barcode),
                p.CostPrice?.ToString("0.00", CultureInfo.InvariantCulture) ?? "", p.StockOnHand.ToString("0.##", CultureInfo.InvariantCulture),
                p.EnableOnWebsite ? "Yes" : "No", p.IsActive ? "Yes" : "No"
            }));
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    // ---- helpers ----

    private static string Describe(ProductCsvRow row) =>
        row.Name ?? (row.Plu is not null ? $"PLU {row.Plu}" : row.Barcode is not null ? $"Barcode {row.Barcode}" : "This line");

    private static Dictionary<string, Product> Index(IReadOnlyList<Product> products, Func<Product, string?> key) =>
        products.Where(p => !string.IsNullOrWhiteSpace(key(p)))
            .GroupBy(p => key(p)!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static string Normalise(string heading) =>
        new(heading.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static decimal? Money(string? cell, string label, List<string> problems)
    {
        if (cell is null) return null;
        var cleaned = cell.Replace("$", "").Replace(",", "").Replace(" ", "");
        if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0)
            return Math.Round(value, 2);
        problems.Add($"{label} “{cell}” isn't an amount.");
        return null;
    }

    private static decimal? Number(string? cell, string label, List<string> problems)
    {
        if (cell is null) return null;
        if (decimal.TryParse(cell.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return value;
        problems.Add($"{label} “{cell}” isn't a number.");
        return null;
    }

    private static bool? YesNo(string? cell, string label, List<string> problems)
    {
        switch (cell?.Trim().ToLowerInvariant())
        {
            case null: return null;
            case "yes" or "y" or "true" or "1" or "on": return true;
            case "no" or "n" or "false" or "0" or "off": return false;
            default:
                problems.Add($"{label} “{cell}” should be Yes or No.");
                return null;
        }
    }

    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // A leading = + - @ would run as a formula in Excel: keep it text.
        var safe = value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return safe.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || safe != value
            ? "\"" + safe.Replace("\"", "\"\"") + "\""
            : safe;
    }

    /// <summary>Comma, unless the heading line has more semicolons or tabs (some Excel set-ups save with ;).</summary>
    private static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var counts = new[] { ',', ';', '\t' }.Select(c => (c, n: firstLine.Count(x => x == c))).OrderByDescending(x => x.n).First();
        return counts.n == 0 ? ',' : counts.c;
    }

    /// <summary>RFC 4180 records: quoted cells may hold the delimiter, "" and line breaks. Line numbers are 1-based.</summary>
    private static IEnumerable<(int Line, List<string> Cells)> SplitRecords(string text, char delimiter)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else { if (c == '\n') line++; cell.Append(c); }
                continue;
            }

            if (c == '"' && cell.Length == 0) inQuotes = true;
            else if (c == delimiter) { cells.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n')
            {
                cells.Add(cell.ToString());
                cell.Clear();
                yield return (recordLine, cells);
                cells = new List<string>();
                line++;
                recordLine = line;
            }
            else cell.Append(c);
        }

        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            yield return (recordLine, cells);
        }
    }
}
