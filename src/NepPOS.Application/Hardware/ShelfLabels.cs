using System.Globalization;
using NepPOS.Application.Reports;

namespace NepPOS.Application.Hardware;

/// <summary>One shelf label: name, price, and the code scanned at the register (barcode, else PLU).</summary>
public sealed record ShelfLabel(string Name, decimal Price, string? Code, string? Unit = null, decimal? WasPrice = null);

/// <summary>
/// Shelf labels as an A4 PDF for any printer: 21 per sheet (3 × 7, Avery L7160 / J8160 size, 63.5 × 38.1 mm), each
/// with the name, a big price and a Code 128 barcode. A promotion's old price prints as "Was $x".
/// </summary>
public static class ShelfLabels
{
    public const int PerSheet = 21;

    private static readonly CultureInfo Au = CultureInfo.GetCultureInfo("en-AU");

    // Avery L7160 in points (1 mm = 2.8346 pt).
    private const float Mm = 2.8346f;
    private const float LabelWidth = 63.5f * Mm, LabelHeight = 38.1f * Mm;
    private const float Left = 7.2f * Mm, Top = 15.15f * Mm, Gap = 2.5f * Mm;

    public static byte[] Pdf(IReadOnlyList<ShelfLabel> labels, int skip = 0)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Count == 0)
            throw new ArgumentException("Add at least one product.", nameof(labels));
        skip = Math.Clamp(skip, 0, PerSheet - 1);   // start part-way down a used sheet

        var pdf = new SimplePdf();
        var slot = skip;
        foreach (var label in labels)
        {
            if (slot == PerSheet)
            {
                pdf.NewPage();
                slot = 0;
            }
            var column = slot % 3;
            var row = slot / 3;
            Draw(pdf, label, Left + column * (LabelWidth + Gap), Top + row * LabelHeight);
            slot++;
        }
        return pdf.ToBytes();
    }

    private static void Draw(SimplePdf pdf, ShelfLabel label, float x, float y)
    {
        const float pad = 6f;
        var inner = LabelWidth - 2 * pad;

        // Name: up to two lines.
        var words = (label.Name ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line1 = "";
        var i = 0;
        for (; i < words.Length; i++)
        {
            var next = line1.Length == 0 ? words[i] : line1 + " " + words[i];
            if (SimplePdf.Width(next, 9, bold: true) > inner) break;
            line1 = next;
        }
        var line2 = SimplePdf.Fit(string.Join(' ', words.Skip(i)), inner, 9, bold: true);
        pdf.Text(x + pad, y + pad + 9, SimplePdf.Fit(line1, inner, 9, bold: true), 9, bold: true);
        if (line2.Length > 0)
            pdf.Text(x + pad, y + pad + 20, line2, 9, bold: true);

        // Price, big, on the right; "Was $x" and the unit under the name.
        var price = label.Price.ToString("C", Au);
        pdf.TextRight(x + LabelWidth - pad, y + 52, price, 22, bold: true);
        if (label.WasPrice is { } was && was > label.Price)
            pdf.Text(x + pad, y + 42, "Was " + was.ToString("C", Au), 8, color: SimplePdf.Colors.Warn);
        if (!string.IsNullOrWhiteSpace(label.Unit))
            pdf.Text(x + pad, y + 52, SimplePdf.Fit(label.Unit!, inner / 2, 7), 7, color: SimplePdf.Colors.Muted);

        // Barcode along the bottom, with its digits.
        if (!string.IsNullOrWhiteSpace(label.Code))
        {
            var modules = Code128.Modules(label.Code!.Trim());
            // 10 modules of white either side (the quiet zone scanners need), 1 pt modules when they fit.
            var total = modules.Sum();
            var module = Math.Min(1.0f, inner / (total + 20f));
            var bx = x + pad + 10 * module;
            var by = y + 60;
            var black = true;
            foreach (var m in modules)
            {
                if (black)
                    pdf.Box(bx, by, m * module, 26, SimplePdf.Colors.Ink);
                bx += m * module;
                black = !black;
            }
            pdf.Text(x + pad, y + 95, label.Code!.Trim(), 7, color: SimplePdf.Colors.Muted);
        }
    }
}

/// <summary>Code 128 (set B: letters, digits and punctuation) as bar/space widths in modules, start to stop.</summary>
public static class Code128
{
    // Values 0–105 (checked against a reference table), then the stop pattern.
    private static readonly string[] Patterns =
    (
        "212222 222122 222221 121223 121322 131222 122213 122312 132212 221213 221312 231212 112232 122132 122231 113222 " +
        "123122 123221 223211 221132 221231 213212 223112 312131 311222 321122 321221 312212 322112 322211 212123 212321 " +
        "232121 111323 131123 131321 112313 132113 132311 211313 231113 231311 112133 112331 132131 113123 113321 133121 " +
        "313121 211331 231131 213113 213311 213131 311123 311321 331121 312113 312311 332111 314111 221411 431111 111224 " +
        "111422 121124 121421 141122 141221 112214 112412 122114 122411 142112 142211 241211 221114 413111 241112 134111 " +
        "111242 121142 121241 114212 124112 124211 411212 421112 421211 212141 214121 412121 111143 111341 131141 114113 " +
        "114311 411113 411311 113141 114131 311141 411131 211412 211214 211232 2331112"
    ).Split(' ');

    private const int StartB = 104, Stop = 106;

    /// <summary>Bar, space, bar… widths for <paramref name="text"/> (ASCII 32–126), with check digit and quiet-zone-free.</summary>
    public static IReadOnlyList<int> Modules(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.Any(c => c < 32 || c > 126))
            throw new ArgumentException("Code 128 set B holds letters, digits and punctuation.", nameof(text));

        var values = new List<int> { StartB };
        values.AddRange(text.Select(c => c - 32));
        var check = StartB;
        for (var i = 1; i < values.Count; i++)
            check += values[i] * i;
        values.Add(check % 103);
        values.Add(Stop);

        return values.SelectMany(v => Patterns[v].Select(c => c - '0')).ToList();
    }
}
