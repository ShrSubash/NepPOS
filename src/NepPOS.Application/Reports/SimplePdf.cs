using System.Globalization;
using System.Text;

namespace NepPOS.Application.Reports;

/// <summary>
/// A tiny PDF writer for A4 reports: text in the two built-in Helvetica fonts, filled boxes and lines.
/// No packages and no printer driver, so it works on any register and is unit tested.
/// Coordinates are in points from the TOP-left corner (y grows downwards), 595 x 842 per page.
/// </summary>
internal sealed class SimplePdf
{
    public const float PageWidth = 595.28f;
    public const float PageHeight = 841.89f;

    private readonly List<StringBuilder> _pages = new();
    private StringBuilder _page = null!;

    public SimplePdf() => NewPage();

    public int PageCount => _pages.Count;

    public void NewPage()
    {
        _page = new StringBuilder();
        _pages.Add(_page);
    }

    /// <summary>Writes onto an earlier page (used for "Page 1 of 3" footers once the page count is known).</summary>
    public void OnPage(int index, Action draw)
    {
        var current = _page;
        _page = _pages[index];
        draw();
        _page = current;
    }

    public void Text(float x, float y, string text, float size, bool bold = false, string color = Colors.Ink)
    {
        _page.Append(CultureInfo.InvariantCulture,
            $"BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {color} rg {x:0.##} {PageHeight - y:0.##} Td ({Escape(text)}) Tj ET\n");
    }

    public void TextRight(float rightX, float y, string text, float size, bool bold = false, string color = Colors.Ink) =>
        Text(rightX - Width(text, size, bold), y, text, size, bold, color);

    public void Box(float x, float y, float width, float height, string fill)
    {
        _page.Append(CultureInfo.InvariantCulture,
            $"{fill} rg {x:0.##} {PageHeight - y - height:0.##} {width:0.##} {height:0.##} re f\n");
    }

    public void Line(float x1, float y, float x2, string color, float thickness = 0.6f)
    {
        _page.Append(CultureInfo.InvariantCulture,
            $"{color} RG {thickness:0.##} w {x1:0.##} {PageHeight - y:0.##} m {x2:0.##} {PageHeight - y:0.##} l S\n");
    }

    /// <summary>Text width in points, from the standard Helvetica font metrics.</summary>
    public static float Width(string text, float size, bool bold = false)
    {
        var widths = bold ? HelveticaBoldWidths : HelveticaWidths;
        var units = 0;
        foreach (var c in text)
            units += c is >= ' ' and <= '~' ? widths[c - ' '] : 556;
        return units * size / 1000f;
    }

    /// <summary>Cuts text with "..." so it fits the width.</summary>
    public static string Fit(string text, float maxWidth, float size, bool bold = false)
    {
        if (Width(text, size, bold) <= maxWidth) return text;
        while (text.Length > 0 && Width(text + "...", size, bold) > maxWidth)
            text = text[..^1];
        return text.TrimEnd() + "...";
    }

    public byte[] ToBytes()
    {
        using var output = new MemoryStream();
        var offsets = new List<long>();

        void Write(string s) { var bytes = Encoding.ASCII.GetBytes(s); output.Write(bytes); }
        void Object(string body)
        {
            offsets.Add(output.Position);
            Write($"{offsets.Count} 0 obj\n");
            Write(body);
            Write("\nendobj\n");
        }

        Write("%PDF-1.4\n");
        output.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }); // marks the file as binary

        // 1 catalog, 2 page tree, 3–4 fonts, then a page + content stream pair per page.
        var pageIds = Enumerable.Range(0, _pages.Count).Select(i => 5 + i * 2).ToList();
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {_pages.Count} >>");
        Object("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Object("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

        foreach (var page in _pages)
        {
            var content = WinAnsi(page.ToString());
            var pageId = offsets.Count + 1;
            Object(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth:0.##} {PageHeight:0.##}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {pageId + 1} 0 R >>"));

            offsets.Add(output.Position);
            Write($"{offsets.Count} 0 obj\n<< /Length {content.Length} >>\nstream\n");
            output.Write(content);
            Write("\nendstream\nendobj\n");
        }

        var xref = output.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return output.ToArray();
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>Page text to bytes in the fonts' WinAnsi encoding (covers $, accents, – and ×).</summary>
    private static byte[] WinAnsi(string text)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            bytes[i] = text[i] switch
            {
                '–' => 0x96,
                '—' => 0x97,
                '’' => 0x92,
                '…' => 0x85,
                '•' => 0x95,
                <= 'ÿ' and var c => (byte)c,
                _ => (byte)'?'
            };
        }
        return bytes;
    }

    // Adobe Helvetica / Helvetica-Bold widths for characters 32 (space) to 126 (~), per 1000 units.
    private static readonly int[] HelveticaWidths =
    {
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584
    };

    private static readonly int[] HelveticaBoldWidths =
    {
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584
    };

    /// <summary>RGB fill/stroke colours as PDF operands.</summary>
    public static class Colors
    {
        public const string Ink = "0.078 0.086 0.169";      // #14162b
        public const string Muted = "0.42 0.435 0.557";      // #6b6f8e
        public const string Line = "0.91 0.914 0.949";       // #e8e9f2
        public const string HeaderFill = "0.957 0.961 0.976"; // #f4f5f9
        public const string Accent = "0.416 0.361 1";        // #6a5cff
        public const string AccentLight = "0.933 0.941 0.984"; // #eef0fb
        public const string Warn = "0.604 0.357 0";          // #9a5b00
        public const string WarnFill = "1 0.957 0.878";      // #fff4e0
        public const string White = "1 1 1";
    }
}
